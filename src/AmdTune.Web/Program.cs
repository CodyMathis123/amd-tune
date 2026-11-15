using System.Security.Cryptography;
using AmdTune.Core;
using AmdTune.GpuWorkloads;
using AmdTune.Infrastructure;
using AmdTune.Web;

// Resolve UI assets independently of the shell's working directory. A published app
// carries wwwroot beside its executable; local dotnet builds may use project assets.
static string FindContentRoot(string baseDirectory)
{
    string[] candidates =
    [
        baseDirectory,
        Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", ".."))
    ];
    foreach (var candidate in candidates)
    {
        if (File.Exists(Path.Combine(candidate, "wwwroot", "index.html")) &&
            File.Exists(Path.Combine(candidate, "wwwroot", "app.js")) &&
            File.Exists(Path.Combine(candidate, "wwwroot", "styles.css")))
            return candidate;
    }

    throw new DirectoryNotFoundException(
        "AMD Tune UI assets missing (wwwroot/index.html, app.js, styles.css). " +
        "Re-publish the complete app; do not launch from an incomplete deployment.");
}

var contentRoot = FindContentRoot(AppContext.BaseDirectory);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = "wwwroot"
});
builder.WebHost.UseUrls("http://127.0.0.1:5783");
var hardware = args.Contains("--hardware", StringComparer.OrdinalIgnoreCase);
var writeRequested = args.Contains("--enable-hardware-writes", StringComparer.OrdinalIgnoreCase);
var verifyGpu = args.Contains("--verify-gpu", StringComparer.OrdinalIgnoreCase);
int? readOnlySamplingIntervalMs = null;
var sampleIntervalIndex = Array.FindIndex(args, a =>
    string.Equals(a, "--telemetry-interval-ms", StringComparison.OrdinalIgnoreCase));
if (sampleIntervalIndex >= 0)
{
    if (!verifyGpu || !args.Contains("--hardware", StringComparer.OrdinalIgnoreCase) || writeRequested)
        throw new InvalidOperationException(
            "--telemetry-interval-ms requires read-only --hardware --verify-gpu. It cannot be used for tuning writes.");
    if (sampleIntervalIndex + 1 >= args.Length ||
        !int.TryParse(args[sampleIntervalIndex + 1], out var requestedInterval) ||
        requestedInterval is < 100 or > 1000)
        throw new ArgumentOutOfRangeException(nameof(args),
            "ADLX telemetry interval must be between 100 and 1000 milliseconds.");
    readOnlySamplingIntervalMs = requestedInterval;
}
var extendedBenchmark = args.Contains("--extended-benchmark", StringComparer.OrdinalIgnoreCase);
var sustainedBenchmark = args.Contains("--sustained-benchmark", StringComparer.OrdinalIgnoreCase);
if (sustainedBenchmark && !hardware)
    throw new InvalidOperationException("--sustained-benchmark requires --hardware.");
if (sustainedBenchmark && extendedBenchmark)
    throw new InvalidOperationException("Choose exactly one of --sustained-benchmark and --extended-benchmark.");
if (extendedBenchmark && !hardware)
    throw new InvalidOperationException("--extended-benchmark requires --hardware.");

if (writeRequested && !hardware) throw new InvalidOperationException("Write flag only valid with --hardware.");
if (hardware && !OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("AMD ADLX hardware mode currently requires Windows.");

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmdTune");
Directory.CreateDirectory(dataDir);
var bridgePath = Path.Combine(AppContext.BaseDirectory, "AmdTune.AdlxBridge.exe");
var recoveryPath = Path.Combine(AppContext.BaseDirectory, "AmdTune.Recovery.exe");

if (hardware)
{
    var bridge = new BridgeProcess(bridgePath);
    builder.Services.AddSingleton(bridge);
    builder.Services.AddSingleton<IGpuDevice, AdlxBridgeGpuDevice>();
    builder.Services.AddSingleton<IRollbackGuard>(_ =>
        new ExternalRecoveryGuard(Path.Combine(dataDir, "recovery"), recoveryPath, bridge));
    builder.Services.AddSingleton<IWorkloadRunner>(sp =>
        sustainedBenchmark
            ? new SustainedGpuBenchmarkRunner(sp.GetRequiredService<IGpuDevice>())
            : new ComputeSharpWorkloadRunner(sp.GetRequiredService<IGpuDevice>(), extendedBenchmark));
    builder.Services.AddSingleton<IWorkloadTelemetryRecorder>(sp =>
        new WorkloadTelemetryRecorder(
            sp.GetRequiredService<IGpuDevice>(),
            readOnlySamplingIntervalMs.HasValue
                ? TimeSpan.FromMilliseconds(readOnlySamplingIntervalMs.Value)
                : null));
}
else
{
    builder.Services.AddSingleton<SimulatedGpuDevice>();
    builder.Services.AddSingleton<IGpuDevice>(sp => sp.GetRequiredService<SimulatedGpuDevice>());
    builder.Services.AddSingleton<IWorkloadRunner, SimulatedWorkloadRunner>();
    builder.Services.AddSingleton<IRollbackGuard, SimulationRollbackGuard>();
}
builder.Services.AddSingleton<IAuditStore>(_ => new JsonAuditStore(Path.Combine(dataDir, "reports")));
builder.Services.AddSingleton(new TunePolicy(new SafetyEnvelope()));
builder.Services.AddSingleton<TuningEngine>();
builder.Services.AddSingleton(sp =>
    new ExperimentManager(sp.GetRequiredService<TuningEngine>(), !hardware || writeRequested));

var app = builder.Build();

// Read-only correctness soak. It deliberately does NOT apply tuning values.
var soakFlagIndex = Array.FindIndex(args, a =>
    string.Equals(a, "--soak-seconds", StringComparison.OrdinalIgnoreCase));
var soakSeconds = 0;
if (soakFlagIndex >= 0)
{
    if (!hardware || writeRequested || !args.Contains("--verify-gpu", StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("--soak-seconds is read-only and requires --hardware --verify-gpu.");
    if (soakFlagIndex + 1 >= args.Length ||
        !int.TryParse(args[soakFlagIndex + 1], out soakSeconds) ||
        soakSeconds is < 30 or > 180)
        throw new ArgumentOutOfRangeException(nameof(soakSeconds), "Use --soak-seconds 30 through 180.");
}

// Command-line read-only workload verification / bounded repeatability soak.
if (args.Contains("--verify-gpu", StringComparer.OrdinalIgnoreCase))
{
    if (!hardware || writeRequested)
        throw new InvalidOperationException("--verify-gpu requires --hardware and forbids hardware writes.");
    var gpu = app.Services.GetRequiredService<IGpuDevice>();
    var runner = app.Services.GetRequiredService<IWorkloadRunner>();
    var telemetryRecorder = app.Services.GetRequiredService<IWorkloadTelemetryRecorder>();
    Console.WriteLine("GPU VERIFY: read-only DX12 tests, no driver tuning writes.");
    Console.WriteLine("ADLX telemetry interval requested: " + (readOnlySamplingIntervalMs ?? 125) +
                      " ms (native metric publication may be slower).");
    Console.WriteLine("Benchmark profile: " + (sustainedBenchmark ? "SUSTAINED-HOST-TIMED" : extendedBenchmark ? "EXTENDED" : "QUICK") +
                      (soakSeconds > 0 ? $"; CORRECTNESS SOAK for >= {soakSeconds}s" : ""));
    var baseline = await gpu.ReadSettingsAsync(CancellationToken.None);
    var sessionId = Guid.NewGuid();
    var sessionStarted = DateTimeOffset.UtcNow;
    var results = new List<WorkloadMeasurement>();
    var passed = false;
    string? diagnosticError = null;
    var final = baseline;
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        int cycle = 0;
        do
        {
            cycle++;
            foreach (var kind in Enum.GetValues<WorkloadKind>())
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                Console.WriteLine($"Running {kind}, cycle {cycle}, original driver settings...");
                var result = await telemetryRecorder.CaptureAsync(
                    token => runner.RunAsync(kind, 49191 + cycle, token),
                    maxHotspotC: 90, timeout.Token);
                results.Add(result);
                Console.WriteLine($"{kind}: GPU={result.ExecutedOnGpu}, correct={result.Correct}, " +
                    $"checked={result.CheckedElements}, mismatches={result.Mismatches}, " +
                    $"score={result.Score:N0} {result.ScoreUnit}, duration={result.ElapsedMilliseconds:N1} ms");
                Console.WriteLine(result.Detail);
                var diagnostics = result.TelemetryEvidence?.Diagnostics;
                Console.WriteLine($"ADLX observed utilization: " +
                    $"{diagnostics?.MinimumUtilizationPercent?.ToString("F1") ?? "missing"}-" +
                    $"{diagnostics?.MaximumUtilizationPercent?.ToString("F1") ?? "missing"}%, " +
                    $"mean={diagnostics?.MeanUtilizationPercent?.ToString("F1") ?? "missing"}%; " +
                    $"distinct metric timestamps={diagnostics?.DistinctMetricSamples ?? 0}/" +
                    $"{diagnostics?.RawSamples ?? 0}; repeated={diagnostics?.RepeatedMetricSamples ?? 0}; " +
                    $"mean metric publication interval=" +
                    $"{diagnostics?.MeanObservedMetricIntervalMs?.ToString("F0") ?? "unknown"} ms");
                Console.WriteLine($"ADLX samples={result.TelemetryEvidence?.SampleCount ?? 0}; " +
                    $"active samples={result.TelemetryEvidence?.ActiveSamples ?? 0}; " +
                    $"loaded mean power={result.TelemetryEvidence?.MeanActiveBoardPowerW?.ToString("F1") ?? "not measured"} W");
                if (!result.ExecutedOnGpu || !result.Correct || result.Mismatches != 0)
                    throw new HardwareException("DX12 result did not pass GPU correctness validation.");
            }
        } while (soakSeconds > 0 && stopwatch.Elapsed.TotalSeconds < soakSeconds);
        passed = true;
    }
    catch (Exception error)
    {
        diagnosticError = error.ToString();
        Console.Error.WriteLine("GPU_VERIFY_FAIL: " + error);
        Environment.ExitCode = 1;
    }
    finally
    {
        try
        {
            final = await gpu.ReadSettingsAsync(CancellationToken.None);
            if (baseline != final)
            {
                diagnosticError = "Driver settings changed during read-only test.";
                passed = false;
                Environment.ExitCode = 1;
                Console.Error.WriteLine("GPU_VERIFY_FAIL: " + diagnosticError);
            }
        }
        catch (Exception error)
        {
            diagnosticError = "Final driver readback failed: " + error;
            passed = false;
            Environment.ExitCode = 1;
            Console.Error.WriteLine("GPU_VERIFY_FAIL: " + diagnosticError);
        }
        Console.WriteLine($"Initial settings: {baseline}");
        Console.WriteLine($"Final settings: {final}");
        var reportDirectory = Path.Combine(dataDir, "soaks");
        Directory.CreateDirectory(reportDirectory);
        var destination = Path.Combine(reportDirectory, sessionId.ToString("N") + ".json");
        var report = new
        {
            SchemaVersion = 1,
            SessionId = sessionId,
            Simulated = false,
            StartedAt = sessionStarted,
            FinishedAt = DateTimeOffset.UtcNow,
            RequestedSoakSeconds = soakSeconds,
            RequestedTelemetryIntervalMs = readOnlySamplingIntervalMs ?? 125,
            BenchmarkProfile = sustainedBenchmark ? "sustained" : extendedBenchmark ? "extended" : "quick",
            OriginalSettings = baseline,
            FinalSettings = final,
            Completed = passed,
            Error = diagnosticError,
            Measurements = results,
            Limitations = "Read-only repeated correctness, not continuous full-load stress. ADLX sampling is asynchronous; host timing is not GPU-only; no long-term stability claim."
        };
        var temp = destination + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp,
                System.Text.Json.JsonSerializer.Serialize(report,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, destination, true);
            Console.WriteLine("Audit: " + destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Console.WriteLine(passed ? (soakSeconds > 0 ? "GPU_SOAK_PASS" : "GPU_VERIFY_PASS") : "GPU_VERIFY_FAIL");
        if (!passed) Environment.ExitCode = 1;
    }
    return;
}

const string origin = "http://127.0.0.1:5783";
var csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

// The app is deliberately loopback-only. These checks protect against DNS rebinding,
// cross-origin requests, and CSRF against hardware-changing endpoints.
app.Use(async (ctx, next) =>
{
    if (!string.Equals(ctx.Request.Host.Value, "127.0.0.1:5783", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    if (HttpMethods.IsPost(ctx.Request.Method))
    {
        if (!string.Equals(ctx.Request.Headers["Origin"].ToString(), origin, StringComparison.Ordinal) ||
            !string.Equals(ctx.Request.Headers["X-AmdTune-CSRF"].ToString(), csrfToken, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 403;
            return;
        }
    }
    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/info", () => new
{
    mode = hardware ? "hardware" : "simulation",
    benchmarkProfile = sustainedBenchmark ? "sustained" : extendedBenchmark ? "extended" : "quick",
    canWrite = !hardware || writeRequested,
    recoveryInstalled = File.Exists(recoveryPath),
    bridgeInstalled = File.Exists(bridgePath),
    csrfToken,
    reportDirectory = dataDir,
    warning = hardware
        ? "EXPERIMENTAL: physical writes require a functioning independent recovery process; no persistent profiles."
        : "SIMULATION ONLY. No actual GPU is queried or changed."
});
app.MapGet("/api/discover", async (IGpuDevice d, CancellationToken ct) => await d.DiscoverAsync(ct));
app.MapGet("/api/plan/memory", async (IGpuDevice d, CancellationToken ct) =>
{
    try
    {
        var caps = await d.DiscoverAsync(ct);
        var original = await d.ReadSettingsAsync(ct);
        return Results.Ok(CandidatePlanner.ConservativeMemorySweep(caps, original));
    }
    catch (SafetyException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/settings", async (IGpuDevice d, CancellationToken ct) => await d.ReadSettingsAsync(ct));
app.MapGet("/api/telemetry", async (IGpuDevice d, CancellationToken ct) => await d.ReadTelemetryAsync(ct));
app.MapGet("/api/plan", async (IGpuDevice d, CancellationToken ct) =>
{
    try
    {
        var caps = await d.DiscoverAsync(ct);
        var original = await d.ReadSettingsAsync(ct);
        return Results.Ok(CandidatePlanner.ConservativeVoltageSweep(caps, original));
    }
    catch (SafetyException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/experiment", (ExperimentManager manager) => manager.Snapshot);
app.MapPost("/api/experiment", async (StartExperimentRequest request, ExperimentManager manager) =>
{
    try
    {
        var started = await manager.StartAsync(request.Candidates, request.Repetitions);
        return started ? Results.Accepted("/api/experiment") : Results.Conflict(new { error = "A trial is already running." });
    }
    catch (SafetyException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/cancel", (ExperimentManager manager) =>
    manager.Cancel() ? Results.Accepted("/api/experiment") : Results.Conflict(new { error = "No active experiment." }));
app.MapGet("/api/report/{id:guid}", (Guid id) =>
{
    var path = Path.Combine(dataDir, "reports", id.ToString("N") + ".json");
    return File.Exists(path)
        ? Results.File(path, "application/json", enableRangeProcessing: false)
        : Results.NotFound();
});

Console.WriteLine(hardware
    ? "[AMD-TUNE] EXPERIMENTAL HARDWARE MODE. Driver writes: " + (writeRequested ? "OPTED IN" : "DISABLED")
    : "[AMD-TUNE] SAFE SIMULATION MODE: no real GPU access.");
Console.WriteLine("[AMD-TUNE] Open " + origin);
await app.RunAsync();

public sealed record StartExperimentRequest(IReadOnlyList<TuningSettings> Candidates, int Repetitions = 3);
