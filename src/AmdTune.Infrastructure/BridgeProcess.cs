using System.Diagnostics;
using System.Text.Json;
using AmdTune.Core;

namespace AmdTune.Infrastructure;

/// <summary>Single typed subprocess boundary; never uses shells or interprets arbitrary arguments.</summary>
public sealed class BridgeProcess(string executablePath)
{
    public string PathOnDisk { get; } = System.IO.Path.GetFullPath(executablePath);

    public async Task<T> InvokeAsync<T>(CancellationToken cancellationToken, params string[] args)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("AMD ADLX native bridge requires Windows.");
        if (!File.Exists(PathOnDisk))
            throw new HardwareException("Missing AMD bridge. Run scripts/build-native.ps1 first.");
        var psi = new ProcessStartInfo(PathOnDisk)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new HardwareException("Could not start native ADLX bridge.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var json = await output;
            var errorText = await stderr;
            if (process.ExitCode != 0)
                throw new HardwareException($"ADLX bridge exited {process.ExitCode}: {errorText} {json}");
            var value = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return value ?? throw new HardwareException("Bridge returned null JSON.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A caller stopping its telemetry sampler is normal; it must not
            // become a hardware fault. The child process still gets terminated.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            // Preserve the original cancellation signal so the sampler can
            // distinguish orderly shutdown from a real ADLX failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw new HardwareException("ADLX bridge timed out after 8 seconds without a caller cancellation.");
        }
        catch (JsonException ex)
        {
            throw new HardwareException("ADLX bridge returned malformed JSON: " + ex.Message);
        }
    }
    /// <summary>
    /// Starts one read-only native ADLX monitoring process per sampled workload,
    /// rather than one process for each sample. Native stdout emits NDJSON.
    /// Any missing/malformed sample or unresponsive helper fails closed.
    /// </summary>
    public async IAsyncEnumerable<GpuTelemetry> StreamTelemetryAsync(
        TimeSpan interval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("AMD ADLX native bridge requires Windows.");
        if (!File.Exists(PathOnDisk))
            throw new HardwareException("Missing AMD bridge; refresh your trusted CI native artifact.");
        if (interval.TotalMilliseconds is < 100 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(interval),
                "Native ADLX telemetry streaming interval must be 100-1000 ms.");

        var psi = new ProcessStartInfo(PathOnDisk)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("telemetry-stream");
        psi.ArgumentList.Add(((int)interval.TotalMilliseconds).ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        using var process = new Process { StartInfo = psi };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new HardwareException("Unable to start ADLX telemetry stream.");
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reading = await ReadTelemetryStreamLineAsync(process, cancellationToken);
                yield return reading;
            }
        }
        finally
        {
            // Never leave a native monitoring process running after a test,
            // cancellation, exception, or a consumer abandoning enumeration.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await process.WaitForExitAsync(reap.Token); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<GpuTelemetry> ReadTelemetryStreamLineAsync(
        Process process, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        string? line;
        try
        {
            line = await process.StandardOutput.ReadLineAsync(budget.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new HardwareException("Native ADLX telemetry stream was silent for 8 seconds.");
        }
        if (line is null)
            throw new HardwareException("Native ADLX telemetry stream exited or closed stdout.");

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new HardwareException("Native ADLX telemetry stream returned a non-object JSON line.");
            if (doc.RootElement.TryGetProperty("error", out var error))
                throw new HardwareException("Native ADLX telemetry stream failed: " + error.GetString());
            if (!doc.RootElement.TryGetProperty("at", out _))
                throw new HardwareException("Unexpected native ADLX telemetry schema; update the native bridge.");
            var reading = JsonSerializer.Deserialize<GpuTelemetry>(line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return reading ?? throw new HardwareException("Native ADLX telemetry stream returned null.");
        }
        catch (JsonException ex)
        {
            throw new HardwareException("Native ADLX stream produced invalid JSON: " + ex.Message);
        }
    }

}
