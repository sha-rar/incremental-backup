using System.Text;
using System.Text.Json;

namespace SnapSafe.Desktop.Model;

public sealed record JobLogEntry(DateTimeOffset TimestampUtc, Guid JobId, string JobName, string Outcome, string Message, double DurationSeconds);
public sealed class JobLogStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public async Task AppendAsync(JobLogEntry entry, CancellationToken token = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        FileStream? lease = null;
        for (var attempt = 0; lease is null; attempt++)
        {
            try { lease = new FileStream(Path.Combine(DirectoryPath, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 39) { await Task.Delay(25, token); }
        }
        await using (lease)
        await using (var output = new FileStream(Path.Combine(DirectoryPath, entry.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, true))
        {
            if (output.Length > 0)
            {
                output.Position = output.Length - 1;
                if (output.ReadByte() != '\n') { output.Position = output.Length; output.WriteByte((byte)'\n'); }
            }
            output.Position = output.Length;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
            await output.WriteAsync(bytes, token); await output.FlushAsync(token); output.Flush(flushToDisk: true);
        }
    }
    public async Task<IReadOnlyList<JobLogEntry>> ReadRecentAsync(CancellationToken token = default)
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var entries = new Queue<JobLogEntry>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.jsonl").OrderDescending(StringComparer.Ordinal).Take(7).Order(StringComparer.Ordinal))
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            using var reader = new StreamReader(input);
            while (await reader.ReadLineAsync(token) is { } line)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<JobLogEntry>(line);
                    if (entry is null) continue;
                    entries.Enqueue(entry); if (entries.Count > 200) entries.Dequeue();
                }
                catch (JsonException) { /* A partial append must not hide later complete entries. */ }
            }
        }
        return entries.OrderByDescending(entry => entry.TimestampUtc).ToArray();
    }
}
