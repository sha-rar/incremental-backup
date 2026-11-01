using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SnapSafe.Core;

var options = new Dictionary<string, string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--help")
    {
        Console.WriteLine("SnapSafe benchmarks [--dataset smoke|small|medium|large] [--root <scratch-parent>] [--output <report.json>] [--keep-data]\nDefault: smoke (16 files, 1 MiB). Small: 100 files/100 MB; medium: 5000/5 GB; large: 50000/50 GB.\nMeasures full, unchanged and changed backups, verified restore, storage, source reads, allocation and peak memory.\nFull datasets require roughly three times their size in free scratch space. Only this run's private directory is cleaned."); return;
    }
    var key = args[i];
    if (options.ContainsKey(key) || key is not ("--dataset" or "--root" or "--output" or "--keep-data")) throw new ArgumentException($"Invalid or duplicate option: {key}");
    options[key] = key == "--keep-data" ? "true" : ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {key}");
}
var dataset = options.GetValueOrDefault("--dataset", "smoke");
var (count, bytes) = dataset switch
{
    "smoke" => (16, 1_048_576L), "small" => (100, 100_000_000L),
    "medium" => (5000, 5_000_000_000L), "large" => (50000, 50_000_000_000L),
    _ => throw new ArgumentException("Unknown dataset.")
};
var parent = Path.GetFullPath(options.GetValueOrDefault("--root", Path.Combine(Path.GetTempPath(), "SnapSafe.Benchmarks")));
Directory.CreateDirectory(parent);
var root = Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    new DestinationProbe().EnsureCapacity(root, checked(bytes * 3 + 64_000_000));
    var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
    var buffer = RandomNumberGenerator.GetBytes(65536);
    for (var index = 0; index < count; index++)
    {
        await using var output = File.Create(Path.Combine(source, $"file-{index:D5}.bin"));
        var remaining = bytes / count + (index < bytes % count ? 1 : 0);
        while (remaining > 0)
        {
            var length = (int)Math.Min(buffer.Length, remaining);
            await output.WriteAsync(buffer.AsMemory(0, length), cancellation.Token); remaining -= length;
        }
    }
    var repository = Path.Combine(root, "backup"); var engine = new BackupEngine(); var plan = new BackupPlan(source, repository);
    long Storage() => Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    var measurements = new List<object>();
    async Task<BackupResult> Measure(string name)
    {
        var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        var result = await engine.CreateSnapshotAsync(plan, cancellationToken: cancellation.Token); watch.Stop();
        measurements.Add(new { operation = name, seconds = watch.Elapsed.TotalSeconds, copiedFiles = result.CopiedFiles, reusedFiles = result.ReusedFiles,
            sourceBytesRead = result.SourceBytesRead, storageBytes = Storage(), allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 });
        return result;
    }
    var initial = await Measure("full");
    if (initial.CopiedFiles != count || initial.SourceBytesRead != bytes) throw new InvalidDataException("Initial backup did not copy/read the expected workload exactly once.");
    var unchanged = await Measure("unchanged");
    if (unchanged.CopiedFiles != 0 || unchanged.ReusedFiles != count) throw new InvalidDataException("Unchanged backup copied data unnecessarily.");
    using (var file = new FileStream(Path.Combine(source, "file-00000.bin"), FileMode.Open, FileAccess.ReadWrite))
    {
        var value = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(value ^ 255));
    }
    var changed = await Measure("one-file-change");
    if (changed.CopiedFiles != 1) throw new InvalidDataException("Changed backup copied an unexpected number of files.");
    var restored = Path.Combine(root, "restored"); var restoreWatch = Stopwatch.StartNew();
    await engine.RestoreAsync(repository, changed.Snapshot.Id, restored, cancellation.Token); restoreWatch.Stop();
    foreach (var path in Directory.EnumerateFiles(source))
    {
        await using var original = File.OpenRead(path);
        await using var copy = File.OpenRead(Path.Combine(restored, "source", Path.GetFileName(path)));
        if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(copy))) throw new InvalidDataException("Restoration integrity mismatch.");
    }
    measurements.Add(new { operation = "restore", seconds = restoreWatch.Elapsed.TotalSeconds, megabytesPerSecond = bytes / 1_000_000d / restoreWatch.Elapsed.TotalSeconds,
        peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 });
    var report = JsonSerializer.Serialize(new { dataset, fileCount = count, logicalBytes = bytes, runDirectory = options.ContainsKey("--keep-data") ? root : null, measurements }, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(report);
    if (options.TryGetValue("--output", out var outputPath)) await File.WriteAllTextAsync(Path.GetFullPath(outputPath), report, cancellation.Token);
}
finally
{
    if (!options.ContainsKey("--keep-data"))
    {
        if (Path.GetDirectoryName(root) != parent || !Path.GetFileName(root).StartsWith("run-", StringComparison.Ordinal)) throw new IOException("Unsafe benchmark cleanup path.");
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to clean a benchmark directory containing a link.");
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        Directory.Delete(root, true);
    }
}
