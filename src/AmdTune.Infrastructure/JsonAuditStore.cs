using System.Text.Json;
using AmdTune.Core;

namespace AmdTune.Infrastructure;

/// <summary>Write-once versioned JSON records, moved atomically into place.</summary>
public sealed class JsonAuditStore(string directory) : IAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
