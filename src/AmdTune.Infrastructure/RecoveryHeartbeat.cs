namespace AmdTune.Infrastructure;

/// <summary>
/// Keeps the lease alive independently of benchmark execution. A failed pulse is
/// recorded and surfaced to the caller; the watchdog will restore the baseline
/// if pulses cease. This class has no GPU or filesystem dependencies of its own.
/// </summary>
public sealed class RecoveryHeartbeat(
    Func<CancellationToken, Task> pulse,
    TimeSpan interval) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private Exception? _failure;

    public void Start()
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        if (_worker is not null)
            throw new InvalidOperationException("Heartbeat already started.");
        _worker = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_stop.Token))
                await pulse(_stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Expected during disarm/disposal.
        }
        catch (Exception error)
        {
            Volatile.Write(ref _failure, error);
        }
    }

    public void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } error)
            throw new InvalidOperationException("Independent recovery heartbeat failed.", error);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_worker is not null) await _worker;
        _stop.Dispose();
    }
}
