using System.Text.Json;
using AmdTune.Core;

namespace AmdTune.Infrastructure;

/// <summary>Write-once versioned JSON records, moved atomically into place.</summary>
public sealed class JsonAuditStore(string directory) : IAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim _journalGate = new(1, 1);

    public async Task AppendEventAsync(Guid sessionId, AuditEvent item, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, sessionId.ToString("N") + ".events.jsonl");
        await _journalGate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(item) + Environment.NewLine, cancellationToken);
        }
        finally { _journalGate.Release(); }
    }

    public async Task SaveAsync(RunReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, report.Id.ToString("N") + ".json");
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var envelope = new
            {
                SchemaVersion = 1,
                AppVersion = typeof(JsonAuditStore).Assembly.GetName().Version?.ToString() ?? "unknown",
                Report = report
            };
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(envelope, JsonOptions), cancellationToken);
            File.Move(temp, destination);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
