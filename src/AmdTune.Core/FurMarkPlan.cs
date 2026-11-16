namespace AmdTune.Core;

/// <summary>
/// A single read-only supervised FurMark 2 Vulkan exercise. The FurMark
/// executable and argument list are intentionally fixed; no arbitrary
/// program or shell arguments are accepted from the operator.
/// </summary>
public sealed record FurMarkPlan(
    string ExecutablePath,
    int DurationSeconds = 60,
    int Width = 2560,
    int Height = 1440)
{
    public const int MaximumSeconds = 600;
    public const int MinimumSeconds = 30;
    public const double MaxHotspotC = 90;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath) ||
            !Path.IsPathFullyQualified(ExecutablePath) ||
            !string.Equals(Path.GetFileName(ExecutablePath), "furmark.exe",
                StringComparison.OrdinalIgnoreCase))
            throw new SafetyException("Specify the absolute path to the installed FurMark 2 furmark.exe.");
        if (DurationSeconds is < MinimumSeconds or > MaximumSeconds)
            throw new SafetyException("Supervised FurMark duration must be 30 to 600 seconds.");
        if (Width is < 640 or > 7680 || Height is < 480 or > 4320 ||
            (long)Width * Height > 33_177_600)
            throw new SafetyException("Invalid FurMark resolution.");
    }

    public IReadOnlyList<string> Arguments()
    {
        Validate();
        return [
            "--demo", "furmark-vk", "--width", Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--height", Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-time", DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--print-render-speed"
        ];
    }
}

public sealed record OwnedProcessResult(
    int ProcessId,
    int? ExitCode,
    bool Aborted,
    bool TimedOut,
    double WallSeconds,
    string StdOutTail,
    string StdErrTail,
    string? FailureReason);

public sealed record SupervisedFurMarkResult(
    Guid SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    FurMarkPlan Plan,
    TuningSettings OriginalSettings,
    TuningSettings? FinalSettings,
    bool Completed,
    string? Error,
    OwnedProcessResult? Process,
    WorkloadTelemetryEvidence? Telemetry,
    ExternalLoadSummary? TelemetrySummary,
    string BinarySha256,
    string ResultClassification,
    IReadOnlyList<string> Limitations);
