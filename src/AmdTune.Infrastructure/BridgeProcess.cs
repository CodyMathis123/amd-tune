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
}
