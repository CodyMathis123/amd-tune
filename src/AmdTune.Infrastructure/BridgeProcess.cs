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
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new HardwareException("ADLX bridge timed out or was cancelled.");
        }
        catch (JsonException ex)
        {
            throw new HardwareException("ADLX bridge returned malformed JSON: " + ex.Message);
        }
    }
}
