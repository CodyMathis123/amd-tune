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
var compareTelemetry = args.Contains("--compare-telemetry", StringComparer.OrdinalIgnoreCase);
var studyFlagIndex = Array.FindIndex(args, a =>
    string.Equals(a, "--study-seconds", StringComparison.OrdinalIgnoreCase));
var studySeconds = 0;
var externalMonitorFlagIndex = Array.FindIndex(args, a =>
    string.Equals(a, "--monitor-external-seconds", StringComparison.OrdinalIgnoreCase));
var externalMonitorSeconds = 0;
var superviseFurMark = args.Contains("--run-furmark", StringComparer.OrdinalIgnoreCase);
var furMarkPathFlag = Array.FindIndex(args, a =>
    string.Equals(a, "--furmark-exe", StringComparison.OrdinalIgnoreCase));
var furMarkSecondsFlag = Array.FindIndex(args, a =>
    string.Equals(a, "--furmark-seconds", StringComparison.OrdinalIgnoreCase));
var labelFlagIndex = Array.FindIndex(args, a =>
    string.Equals(a, "--external-load-label", StringComparison.OrdinalIgnoreCase));
var externalLoadLabel = "unspecified";
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
if (compareTelemetry &&
    (!hardware || writeRequested || !verifyGpu || !sustainedBenchmark ||
     extendedBenchmark || sampleIntervalIndex >= 0 ||
     args.Contains("--soak-seconds", StringComparer.OrdinalIgnoreCase)))
    throw new InvalidOperationException(
        "--compare-telemetry requires read-only --hardware --verify-gpu --sustained-benchmark; " +
        "do not add --telemetry-interval-ms, --soak-seconds, or hardware write flags.");
if (hardware && !OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("AMD ADLX hardware mode currently requires Windows.");
if (superviseFurMark)
{
    if (!hardware || writeRequested || verifyGpu || sustainedBenchmark ||
        extendedBenchmark || compareTelemetry || studyFlagIndex >= 0 ||
        externalMonitorFlagIndex >= 0 || labelFlagIndex >= 0 ||
        sampleIntervalIndex >= 0 ||
        args.Contains("--soak-seconds", StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "--run-furmark requires read-only --hardware and cannot be combined with other testing or write flags.");
    if (furMarkPathFlag < 0 || furMarkPathFlag + 1 >= args.Length)
        throw new InvalidOperationException("--run-furmark requires --furmark-exe with an absolute path.");
    if (furMarkSecondsFlag >= 0 && (furMarkSecondsFlag + 1 >= args.Length ||
        !int.TryParse(args[furMarkSecondsFlag + 1], out var value) ||
        value is < FurMarkPlan.MinimumSeconds or > FurMarkPlan.MaximumSeconds))
        throw new InvalidOperationException("--furmark-seconds must be between 30 and 600.");
}
else if (furMarkPathFlag >= 0 || furMarkSecondsFlag >= 0)
    throw new InvalidOperationException("FurMark-specific options require --run-furmark.");

if (labelFlagIndex >= 0)
{
    if (externalMonitorFlagIndex < 0 || labelFlagIndex + 1 >= args.Length ||
        string.IsNullOrWhiteSpace(args[labelFlagIndex + 1]) ||
        args[labelFlagIndex + 1].Length > 100)
        throw new InvalidOperationException(
            "--external-load-label requires --monitor-external-seconds and a short label (1-100 characters).");
    externalLoadLabel = args[labelFlagIndex + 1];
}
if (externalMonitorFlagIndex >= 0)
{
    if (!hardware || writeRequested || verifyGpu || sustainedBenchmark ||
        extendedBenchmark || compareTelemetry || studyFlagIndex >= 0 ||
        sampleIntervalIndex >= 0 ||
        args.Contains("--soak-seconds", StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "--monitor-external-seconds requires read-only --hardware, without benchmark, verification, telemetry override or hardware-write flags.");
    if (externalMonitorFlagIndex + 1 >= args.Length ||
        !int.TryParse(args[externalMonitorFlagIndex + 1], out externalMonitorSeconds) ||
        externalMonitorSeconds is < ExternalLoadAnalysis.MinimumDurationSeconds or >
            ExternalLoadAnalysis.MaximumDurationSeconds)
        throw new ArgumentOutOfRangeException(nameof(args),
            "External GPU load monitoring duration must be 30 to 900 seconds.");
}

if (studyFlagIndex >= 0)
{
    if (!hardware || writeRequested || !verifyGpu || !sustainedBenchmark ||
        extendedBenchmark || compareTelemetry || sampleIntervalIndex >= 0 ||
        args.Contains("--soak-seconds", StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "--study-seconds requires read-only --hardware --verify-gpu --sustained-benchmark; " +
            "cannot combine with telemetry overrides, compare mode, soak mode or write flags.");
    if (studyFlagIndex + 1 >= args.Length ||
        !int.TryParse(args[studyFlagIndex + 1], out studySeconds) ||
        studySeconds is < ReadOnlyStudy.MinimumSeconds or > ReadOnlyStudy.MaximumSeconds)
        throw new ArgumentOutOfRangeException(nameof(args),
            $"Use --study-seconds {ReadOnlyStudy.MinimumSeconds} to {ReadOnlyStudy.MaximumSeconds}.");
}

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

// Supervised FurMark 2 Vulkan stress test with bounded process lifetime.
// We own the child process and can terminate it on an ADLX thermal or sensor
// failure. No driver-tuning writes or automatic candidate sweeps are enabled.
if (superviseFurMark)
{
    var duration = furMarkSecondsFlag >= 0 ? int.Parse(
        args[furMarkSecondsFlag + 1], System.Globalization.CultureInfo.InvariantCulture) : 60;
    var plan = new FurMarkPlan(args[furMarkPathFlag + 1], duration);
    plan.Validate();
    var supervisor = new FurMarkSupervisor(app.Services.GetRequiredService<IGpuDevice>());
    using var operatorStop = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (_, e) =>
    {
        e.Cancel = true;
        operatorStop.Cancel();
    };
    Console.CancelKeyPress += handler;
    Console.WriteLine("FURMARK 2 SUPERVISED STRESS: READ ONLY; no GPU tuning changes.");
    Console.WriteLine($"Launching: {plan.ExecutablePath} (Vulkan {plan.Width}x{plan.Height}; {plan.DurationSeconds}s)");
    Console.WriteLine($"Hotspot guard: {FurMarkPlan.MaxHotspotC:F0}C. Test process will be terminated on guard or sensor failure.");
    Console.WriteLine("Note: FurMark CLI exit status cannot certify rendered-frame correctness or gaming stability.");
    try
    {
        var report = await supervisor.RunAsync(plan, operatorStop.Token);
        var folder = Path.Combine(dataDir, "soaks");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, report.SessionId.ToString("N") + ".furmark.json");
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp,
                System.Text.Json.JsonSerializer.Serialize(report,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        Console.WriteLine($"FurMark PID: {report.Process?.ProcessId}; exit={report.Process?.ExitCode}; " +
            $"process time={report.Process?.WallSeconds.ToString("F1") ?? "unknown"}s, " +
            $"aborted={report.Process?.Aborted}, timeout={report.Process?.TimedOut}");
        Console.WriteLine($"ADLX samples: {report.TelemetrySummary?.Samples ?? 0}; " +
            $"distinct value states={report.TelemetrySummary?.DistinctMetricValueStates ?? 0}; " +
            $"mean GPU utilization={report.TelemetrySummary?.MeanGpuUtilizationPercent?.ToString("F1") ?? "missing"}%; " +
            $"peak hotspot={report.TelemetrySummary?.PeakHotspotC?.ToString("F1") ?? "missing"}C; " +
            $"mean board power={report.TelemetrySummary?.MeanBoardPowerW?.ToString("F1") ?? "missing"} W");
        Console.WriteLine($"Classification: {report.ResultClassification}");
        Console.WriteLine($"Original settings: {report.OriginalSettings}");
        Console.WriteLine($"Final settings: {report.FinalSettings}");
        Console.WriteLine("Audit: " + path);
        if (!string.IsNullOrWhiteSpace(report.Process?.StdOutTail))
            Console.WriteLine("FurMark stdout tail (unverified rendering statistics):\n" + report.Process.StdOutTail);
        if (!string.IsNullOrWhiteSpace(report.Process?.StdErrTail))
            Console.WriteLine("FurMark stderr tail:\n" + report.Process.StdErrTail);
        Console.WriteLine(report.Completed ? "FURMARK_SUPERVISED_PASS" : "FURMARK_SUPERVISED_FAIL");
        if (!report.Completed)
        {
            Console.Error.WriteLine(report.Error ?? "FurMark supervision incomplete.");
            Environment.ExitCode = 1;
        }
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("FURMARK_SUPERVISED_FAIL: " + error);
        Environment.ExitCode = 1;
    }
    finally
    {
        Console.CancelKeyPress -= handler;
    }
    return;
}

// Passive, read-only external GPU load monitor. The user launches/stops
// OCCT, 3DMark, a game or another tool separately. No GPU write commands and
// crucially NO ability to terminate an external stress program on guard failure.
if (externalMonitorSeconds > 0)
{
    var device = app.Services.GetRequiredService<IGpuDevice>();
    var original = await device.ReadSettingsAsync(CancellationToken.None);
    var final = original;
    var started = DateTimeOffset.UtcNow;
    var session = Guid.NewGuid();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    WorkloadTelemetryEvidence? evidence = null;
    ExternalLoadSummary? summary = null;
    string? error = null;
    var passed = false;

    using var monitorCancellation = new CancellationTokenSource();
    await using var sampler = new WorkloadTelemetrySampler(
        device, 90, monitorCancellation, TimeSpan.FromMilliseconds(125),
        ExternalLoadAnalysis.MaxRetainedSamples);
    ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        monitorCancellation.Cancel();
    };
    Console.CancelKeyPress += onCancel;
    Console.WriteLine($"EXTERNAL GPU LOAD MONITOR: passive ADLX monitoring for {externalMonitorSeconds}s.");
    Console.WriteLine("No driver tuning writes. Start your external GPU stress test now.");
    Console.WriteLine("WARNING: Thermal/sensor guard failure stops THIS MONITOR, not the external workload.");
    Console.WriteLine("Stop the external workload yourself on a warning or any system instability.");
    Console.WriteLine($"Original tuning: {original}");
    Console.WriteLine($"External tool label: {externalLoadLabel} (self-reported, not verified).");
    try
    {
        sampler.Start();
        while (clock.Elapsed.TotalSeconds < externalMonitorSeconds)
        {
            var remaining = externalMonitorSeconds - clock.Elapsed.TotalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, Math.Max(0.001, remaining))),
                monitorCancellation.Token);
            if (clock.Elapsed.TotalSeconds < externalMonitorSeconds)
                Console.WriteLine($"Monitoring... {clock.Elapsed.TotalSeconds:F0}/{externalMonitorSeconds} seconds");
        }
        passed = true;
    }
    catch (OperationCanceledException)
    {
        error = "Monitoring was interrupted by the operator or a sensor safety failure.";
        Console.Error.WriteLine("GPU_EXTERNAL_MONITOR_INTERRUPTED: " + error);
        Environment.ExitCode = 1;
    }
    catch (Exception ex)
    {
        error = ex.ToString();
        Console.Error.WriteLine("GPU_EXTERNAL_MONITOR_FAIL: " + ex);
        Environment.ExitCode = 1;
    }
    finally
    {
        try
        {
            evidence = await sampler.StopAsync();
            if (passed)
            {
                if (evidence.SampleCount < 3)
                    throw new SafetyException("External load monitoring had fewer than three ADLX readings.");
                summary = ExternalLoadAnalysis.Analyze(evidence.Samples, evidence.StartedAt, evidence.FinishedAt);
            }
        }
        catch (Exception ex)
        {
            passed = false;
            error = ex.ToString();
            Environment.ExitCode = 1;
            Console.Error.WriteLine("GPU_EXTERNAL_MONITOR_FAIL: " + ex);
            Console.Error.WriteLine(
                "STOP THE EXTERNAL LOAD NOW: this process cannot terminate the GPU stress tool.");
        }

        try
        {
            final = await device.ReadSettingsAsync(CancellationToken.None);
            if (final != original)
            {
                passed = false;
                error = "Read-only monitoring detected changed driver tuning settings.";
                Environment.ExitCode = 1;
                Console.Error.WriteLine("GPU_EXTERNAL_MONITOR_FAIL: " + error);
            }
        }
        catch (Exception ex)
        {
            passed = false;
            error = "Final driver settings readback failed: " + ex;
            Environment.ExitCode = 1;
            Console.Error.WriteLine("GPU_EXTERNAL_MONITOR_FAIL: " + error);
        }
        clock.Stop();
        Console.CancelKeyPress -= onCancel;

        var folder = Path.Combine(dataDir, "soaks");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, session.ToString("N") + ".external-load.json");
        var report = new
        {
            SchemaVersion = 1,
            SessionId = session,
            Kind = "read-only-external-gpu-load-monitor",
            ExternalLoadLabel = externalLoadLabel,
            Simulated = false,
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            RequestedDurationSeconds = externalMonitorSeconds,
            ActualDurationSeconds = clock.Elapsed.TotalSeconds,
            OriginalSettings = original,
            FinalSettings = final,
            Completed = passed,
            Error = error,
            Summary = summary,
            TelemetryEvidence = evidence,
            Limitations =
                "This monitor does not launch, validate, stop or terminate the external stress application. " +
                "ADLX sampling is asynchronous and not calibrated GPU-only timing. " +
                "GPU correctness/error counts must come from the external testing tool. " +
                "No GPU tuning writes, no automatic overclock approval."
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
        if (summary is not null)
        {
            Console.WriteLine(
                $"ADLX samples={summary.Samples}, source timestamps={summary.SourceTimestampDistinctSamples}, " +
                $"distinct sensor states={summary.DistinctMetricValueStates}; " +
                $"utilization mean={summary.MeanGpuUtilizationPercent?.ToString("F1") ?? "n/a"}%, " +
                $"peak={summary.PeakGpuUtilizationPercent?.ToString("F1") ?? "n/a"}%; " +
                $"power mean={summary.MeanBoardPowerW?.ToString("F1") ?? "n/a"}W, " +
                $"peak={summary.PeakBoardPowerW?.ToString("F1") ?? "n/a"}W; " +
                $"hotspot max={summary.PeakHotspotC?.ToString("F1") ?? "n/a"}C.");
            foreach (var minute in summary.Minutes)
                Console.WriteLine(
                    $"Minute {minute.Minute + 1}: N={minute.Samples}, " +
                    $"GPU utilization mean={minute.MeanGpuUtilizationPercent?.ToString("F1") ?? "n/a"}%, " +
                    $"board power mean={minute.MeanBoardPowerW?.ToString("F1") ?? "n/a"}W, " +
                    $"hotspot peak={minute.PeakHotspotC?.ToString("F1") ?? "n/a"}C.");
            if (summary.HighUtilizationSampleFraction < 0.5)
                Console.WriteLine("LOAD QUALITY WARNING: Fewer than half of readings showed >=80% GPU utilization. " +
                                  "This is not evidence of a sustained heavy GPU load.");
        }
        Console.WriteLine("Final tuning: " + final);
        Console.WriteLine(passed ? "GPU_EXTERNAL_MONITOR_PASS" : "GPU_EXTERNAL_MONITOR_FAIL");
        if (!passed) Environment.ExitCode = 1;
    }
    return;
}

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

// Five-minute (or operator-selected) read-only measurement study.
// This reuses the bounded 750ms GPU work dispatches and performs full CPU
// correctness checks between them, not an uninterrupted GPU saturation test.
if (studySeconds > 0)
{
    var device = app.Services.GetRequiredService<IGpuDevice>();
    var runner = app.Services.GetRequiredService<IWorkloadRunner>();
    var initialSettings = await device.ReadSettingsAsync(CancellationToken.None);
    var finalSettings = initialSettings;
    var session = Guid.NewGuid();
    var start = DateTimeOffset.UtcNow;
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var records = new List<StudyObservation>();
    StudySummary? summary = null;
    string? error = null;
    var passed = false;
    var blocks = 0;
    Console.WriteLine($"GPU MEASUREMENT STUDY: {studySeconds} seconds minimum, read-only.");
    Console.WriteLine($"Intervals: {string.Join(", ", ReadOnlyStudy.IntervalsMs)} ms; balanced rotating blocks.");
    Console.WriteLine("All GPU writes disabled; each reading has 90C hotspot abort guard.");
    Console.WriteLine($"Original driver settings: {initialSettings}");
    try
    {
        for (var block = 0; block < ReadOnlyStudy.MaxBlocks; block++)
        {
            if (block > 0 && watch.Elapsed.TotalSeconds >= studySeconds)
                break;
            var schedule = ReadOnlyStudy.Schedule(block);
            var blockStart = DateTimeOffset.UtcNow;
            for (var pos = 0; pos < schedule.Count; pos++)
            {
                var (kind, intervalMs) = schedule[pos];
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var recorder = new WorkloadTelemetryRecorder(
                    device, TimeSpan.FromMilliseconds(intervalMs));
                var measureStart = DateTimeOffset.UtcNow;
                var measurement = await recorder.CaptureAsync(
                    token => runner.RunAsync(kind, 49191 + block, token),
                    maxHotspotC: 90,
                    budget.Token);
                var measureFinish = DateTimeOffset.UtcNow;
                records.Add(new StudyObservation(
                    block, pos, kind, intervalMs, measureStart, measureFinish, measurement));

                // Hard fail: any wrong shader output, missing GPU execution,
                // failed ADLX telemetry or thermal excursion aborts the study.
                if (!measurement.ExecutedOnGpu || !measurement.Correct ||
                    measurement.Mismatches != 0 || !double.IsFinite(measurement.Score) ||
                    measurement.Score <= 0)
                    throw new HardwareException(
                        $"GPU correctness failure in block {block + 1}: {kind} at {intervalMs}ms.");
            }
            blocks++;
            var observedSettings = await device.ReadSettingsAsync(CancellationToken.None);
            if (observedSettings != initialSettings)
                throw new SafetyException(
                    $"Driver tuning changed during read-only study: {initialSettings} -> {observedSettings}.");
            var recent = records.Skip(records.Count - schedule.Count).ToArray();
            var hottest = recent.Select(r => r.Measurement.TelemetryEvidence?.PeakHotspotC)
                .Where(c => c is > 0).Select(c => c!.Value)
                .DefaultIfEmpty(double.NaN).Max();
            var unique = recent.Sum(r =>
                r.Measurement.TelemetryEvidence?.Diagnostics?.DistinctSamplesWithinHostTimedWindow ?? 0);
            Console.WriteLine(
                $"Block {block + 1} complete: elapsed={watch.Elapsed.TotalSeconds:F1}s / {studySeconds}s; " +
                $"workloads={records.Count}, all correct, " +
                $"host GPU timing={recent.Sum(r => r.Measurement.ElapsedMilliseconds)/1000:F1}s; " +
                $"in-window unique ADLX samples={unique}; " +
                $"peak sampled hotspot={(double.IsFinite(hottest) ? hottest.ToString("F1") : "unknown")}C; " +
                $"block wall-time={(DateTimeOffset.UtcNow - blockStart).TotalSeconds:F1}s");
        }
        if (watch.Elapsed.TotalSeconds < studySeconds)
            throw new SafetyException("Reached maximum measurement blocks before the requested study duration.");
        summary = ReadOnlyStudy.Summarize(records);
        passed = true;
    }
    catch (Exception ex)
    {
        error = ex.ToString();
        Environment.ExitCode = 1;
        Console.Error.WriteLine("GPU_STUDY_FAIL: " + ex);
    }
    finally
    {
        watch.Stop();
        try
        {
            finalSettings = await device.ReadSettingsAsync(CancellationToken.None);
            if (finalSettings != initialSettings)
            {
                passed = false;
                error = "Read-only study detected changed driver settings: " + finalSettings;
                Environment.ExitCode = 1;
                Console.Error.WriteLine("GPU_STUDY_FAIL: " + error);
            }
        }
        catch (Exception ex)
        {
            passed = false;
            error = "Final driver readback failed: " + ex;
            Environment.ExitCode = 1;
            Console.Error.WriteLine("GPU_STUDY_FAIL: " + error);
        }
        if (passed && summary is not null)
        {
            Console.WriteLine(
                $"Study totals: {summary.TotalBlocks} complete blocks, " +
                $"{summary.TotalMeasurements} correctly verified workloads, " +
                $"{summary.TotalHostTimedSeconds:F1}s of host-timed GPU work; " +
                $"total wall-time={watch.Elapsed.TotalSeconds:F1}s.");
            foreach (var condition in summary.Conditions)
            {
                var stats = condition.Throughput;
                Console.WriteLine(
                    $"{condition.Kind} @{condition.PollIntervalMs}ms: " +
                    $"N={condition.ScoredMeasurements} (plus warmup); " +
                    $"mean={stats?.Mean.ToString("N0") ?? "n/a"}, " +
                    $"median={stats?.Median.ToString("N0") ?? "n/a"}, " +
                    $"CV={stats?.CoefficientOfVariationPercent.ToString("F2") ?? "n/a"}%; " +
                    $"later-vs-earlier={condition.LaterHalfVsEarlyHalfPercent?.ToString("+0.00;-0.00;0.00") ?? "n/a"}%; " +
                    $"in-window ADLX={condition.DistinctInWindowSamples}, " +
                    $"active={condition.ActiveInWindowSamples}, " +
                    $"peak hotspot={condition.PeakObservedHotspotC?.ToString("F1") ?? "n/a"}C");
            }
            foreach (var pair in summary.PairedComparisons)
                Console.WriteLine(
                    $"PAIRED {pair.Kind} {pair.ComparisonIntervalMs}/{pair.ReferenceIntervalMs}ms " +
                    $"N={pair.PairedBlocks}: mean difference=" +
                    $"{pair.PairedPercentDifferences?.Mean.ToString("+0.00;-0.00;0.00") ?? "n/a"}%, " +
                    $"CV not interpreted as statistical confidence.");
        }

        var folder = Path.Combine(dataDir, "soaks");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, session.ToString("N") + ".study.json");
        var report = new
        {
            SchemaVersion = 1,
            SessionId = session,
            Kind = "read-only-five-minute-telemetry-study",
            Simulated = false,
            StartedAt = start,
            FinishedAt = DateTimeOffset.UtcNow,
            RequestedDurationSeconds = studySeconds,
            ActualWallSeconds = watch.Elapsed.TotalSeconds,
            CompletedBlocks = blocks,
            Observations = records,
            Summary = summary,
            OriginalSettings = initialSettings,
            FinalSettings = finalSettings,
            Completed = passed,
            Error = error,
            Limitations =
                "Time-balanced 125/250/500ms monitoring comparisons with first block discarded " +
                "for scoring; GPU synthetic host time only. Full CPU verification gaps prevent " +
                "continuous 5-minute GPU load. No voltage, VRAM or memory timing writes. " +
                "Not a certified stability/efficiency test; GPU timestamp queries not implemented."
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
        Console.WriteLine($"Final driver settings: {finalSettings}");
        Console.WriteLine(passed ? "GPU_STUDY_PASS" : "GPU_STUDY_FAIL");
        if (!passed) Environment.ExitCode = 1;
    }
    return;
}

// Read-only ABBA/BAAB monitoring interference comparison. This mode does
// NOT write GPU settings. It compares *measurement conditions*, not tuned
// against stock, and is not a significance test.
if (compareTelemetry)
{
    var gpu = app.Services.GetRequiredService<IGpuDevice>();
    var runner = app.Services.GetRequiredService<IWorkloadRunner>();
    var original = await gpu.ReadSettingsAsync(CancellationToken.None);
    var finalSettings = original;
    var id = Guid.NewGuid();
    var started = DateTimeOffset.UtcNow;
    var observations = new List<MonitoringObservation>();
    IReadOnlyList<MonitoringKindSummary>? summaries = null;
    string? error = null;
    var passed = false;

    Console.WriteLine("GPU MONITOR COMPARISON: READ-ONLY ABBA/BAAB; no hardware tuning writes.");
    Console.WriteLine($"Original driver settings: {original}");
    try
    {
        foreach (var kind in Enum.GetValues<WorkloadKind>())
        {
            for (var round = 0; round < MonitoringComparison.Rounds; round++)
            {
                var sequence = MonitoringComparison.IntervalsForRound(round);
                for (var position = 0; position < sequence.Count; position++)
                {
                    var interval = sequence[position];
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    var recorder = new WorkloadTelemetryRecorder(
                        gpu, TimeSpan.FromMilliseconds(interval));
                    // Pair the seed within each crossover round; never alter
                    // tuning state to select a telemetry interval.
                    var result = await recorder.CaptureAsync(
                        token => runner.RunAsync(kind, 49191 + round, token),
                        maxHotspotC: 90, timeout.Token);
                    observations.Add(new MonitoringObservation(
                        kind, round, position, interval, result));
                    var diag = result.TelemetryEvidence?.Diagnostics;
                    Console.WriteLine(
                        $"{kind} round={round + 1}, slot={position + 1}, sampling={interval}ms: " +
                        $"correct={result.Correct}, errors={result.Mismatches}, " +
                        $"score={result.Score:N0}, window={result.ElapsedMilliseconds:F1}ms, " +
                        $"distinct ADLX={diag?.DistinctMetricSamples ?? 0}, " +
                    $"in timed host window={diag?.SamplesWithinHostTimedWindow ?? 0}/{result.TelemetryEvidence?.SampleCount ?? 0}, " +
                        $"utilization mean={diag?.MeanUtilizationPercent?.ToString("F1") ?? "missing"}%");
                    if (!result.ExecutedOnGpu || !result.Correct || result.Mismatches != 0)
                        throw new HardwareException(
                            "GPU correctness failure during read-only monitoring comparison.");
                }
            }
        }
        summaries = MonitoringComparison.Summarize(observations);
        passed = true;
    }
    catch (Exception ex)
    {
        error = ex.ToString();
        Console.Error.WriteLine("GPU_MONITOR_COMPARE_FAIL: " + ex);
        Environment.ExitCode = 1;
    }
    finally
    {
        try
        {
            finalSettings = await gpu.ReadSettingsAsync(CancellationToken.None);
            if (finalSettings != original)
            {
                error = "Read-only monitoring comparison detected changed GPU driver settings.";
                passed = false;
                Environment.ExitCode = 1;
                Console.Error.WriteLine("GPU_MONITOR_COMPARE_FAIL: " + error);
            }
        }
        catch (Exception ex)
        {
            error = "Final driver settings readback failed: " + ex;
            passed = false;
            Environment.ExitCode = 1;
            Console.Error.WriteLine("GPU_MONITOR_COMPARE_FAIL: " + error);
        }

        if (passed && summaries is not null)
            foreach (var summary in summaries)
                Console.WriteLine(
                    $"{summary.Kind}: 125ms mean={summary.Fast.Mean:N0} CV={summary.Fast.CoefficientOfVariationPercent:F2}%, " +
                    $"500ms mean={summary.Slow.Mean:N0} CV={summary.Slow.CoefficientOfVariationPercent:F2}%; " +
                    $"500-vs-125={summary.SlowVsFastPercent:+0.00;-0.00;0.00}%; " +
                    $"distinct ADLX samples={summary.FastDistinctSourceSamples}/{summary.SlowDistinctSourceSamples}.");

        var reportDirectory = Path.Combine(dataDir, "soaks");
        Directory.CreateDirectory(reportDirectory);
        var destination = Path.Combine(reportDirectory, id.ToString("N") + ".comparison.json");
        var report = new
        {
            SchemaVersion = 1,
            SessionId = id,
            Simulated = false,
            Kind = "read-only-telemetry-crossover",
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            OriginalSettings = original,
            FinalSettings = finalSettings,
            Completed = passed,
            Error = error,
            Schedule = "ABBA/BAAB per workload; 125ms vs 500ms, two crossover rounds",
            Observations = observations,
            Summaries = summaries,
            Limitations =
                "Host-dispatch timings only, not GPU timestamps. Four observations per condition; " +
                "no confidence interval or causal claim. Temperature/power are asynchronous ADLX snapshots. " +
                "Does not approve automatic tuning or establish long-term stability."
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
        Console.WriteLine($"Final driver settings: {finalSettings}");
        Console.WriteLine(passed ? "GPU_MONITOR_COMPARE_PASS" : "GPU_MONITOR_COMPARE_FAIL");
        if (!passed) Environment.ExitCode = 1;
    }
    return;
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
                Console.WriteLine($"ADLX during timed host window: " +
                    $"{diagnostics?.SamplesWithinHostTimedWindow ?? 0}/{result.TelemetryEvidence?.SampleCount ?? 0}; " +
                    $"unique source samples={diagnostics?.DistinctSamplesWithinHostTimedWindow ?? 0}; " +
                    $"scoped={diagnostics?.HasHostTimedWindow ?? false}");
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
