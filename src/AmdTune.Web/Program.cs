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
    builder.Services.AddSingleton<IWorkloadRunner, ComputeSharpWorkloadRunner>();
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
