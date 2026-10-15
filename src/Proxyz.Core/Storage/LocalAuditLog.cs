using System.Text.Json;

namespace Proxyz.Core.Storage;

public sealed class LocalAuditLog(string directory) : IAuditLog
{
    private readonly object _gate = new();
    public void Record(string action, string outcome, Guid? profileId = null)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "events.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, action, outcome, profileId }) + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A nonessential audit failure must not turn a successful OS rollback into a failure.
                System.Diagnostics.Trace.WriteLine("Proxyz: local audit unavailable.");
            }
        }
    }
}
