using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AmdTune.Core;

namespace AmdTune.Infrastructure;

/// <summary>
/// Owns one explicitly selected child process; handles timeout, caller
/// cancellation and child-tree cleanup. No shell or arbitrary argument
/// interpretation. This is NOT a GPU recovery watchdog.
/// </summary>
public static class OwnedProcessRunner
{
    private const int MaximumOutputCharacters = 32_768;

    public static async Task<OwnedProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan deadline,
        CancellationToken stop)
    {
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(deadline));
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The explicitly chosen stress executable was not found.", executable);
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = psi };
        stop.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start()) throw new HardwareException("Cannot launch the selected stress executable.");
        }
        catch (Win32Exception error)
        {
            throw new HardwareException("Cannot launch the selected stress executable: " + error.Message);
        }

        var watch = Stopwatch.StartNew();
        // Drain both streams continuously: a child must not block on full
        // stdout/stderr pipes. Preserve only a bounded diagnostic tail.
        var stdout = DrainTailAsync(process.StandardOutput);
        var stderr = DrainTailAsync(process.StandardError);
        bool aborted = false, timedOut = false;
        string? reason = null;
        using var timer = new CancellationTokenSource(deadline);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop, timer.Token);
        try
        {
            await process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = timer.IsCancellationRequested && !stop.IsCancellationRequested;
            aborted = true;
            reason = timedOut
                ? $"FurMark exceeded the supervised process deadline ({deadline.TotalSeconds:F0}s)."
                : "Monitoring was interrupted, a telemetry safety guard failed, or the operator cancelled.";
        }
        finally
        {
            if (!process.HasExited)
            {
                aborted = true;
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception error) { reason = "Failed to terminate the owned process: " + error.Message; }
            }
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(reap.Token); }
            catch (OperationCanceledException)
            {
                aborted = true;
                reason = "Owned process did not exit within five seconds after termination was requested.";
            }
        }
        watch.Stop();
        var drained = await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        var outTail = drained[0];
        var errTail = drained[1];
        return new OwnedProcessResult(process.Id, process.HasExited ? process.ExitCode : null,
            aborted, timedOut, watch.Elapsed.TotalSeconds, outTail, errTail, reason);
    }

    private static async Task<string> DrainTailAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 2048) line = line[..2048] + " [line truncated]";
            result.AppendLine(line);
            if (result.Length > MaximumOutputCharacters)
                result.Remove(0, result.Length - MaximumOutputCharacters);
        }
        return result.ToString();
    }
}
