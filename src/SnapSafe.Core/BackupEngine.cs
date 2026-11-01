using System.Text.Json;

namespace SnapSafe.Core;

public sealed class BackupEngine
{
    public async Task<BackupResult> CreateSnapshotAsync(BackupPlan plan, bool fullBackup = false,
        IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        plan = plan.ValidateForBackup();
        using var repository = new SnapshotRepository(plan.DestinationDirectory, create: true);
        repository.RemoveStaleStaging(cancellationToken);
        var scan = DirectoryScanner.Scan(plan, cancellationToken);
        var previous = fullBackup ? null : await repository.LatestAsync(cancellationToken);
        var previousFiles = previous?.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal)
            ?? new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(repository.SnapshotsDirectory, ".pending-" + id);
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<SnapshotFile>();
            var copied = 0;
            foreach (var file in scan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var input = FileIntegrity.OpenRead(file.FullPath);
                var length = input.Length;
                var hash = await FileIntegrity.HashAsync(input, cancellationToken);
                if (previousFiles.TryGetValue(file.RelativePath, out var old) && old.Length == length && old.Sha256 == hash)
                {
                    // Do not propagate a damaged previous copy into another completed snapshot.
                    await FileIntegrity.VerifyAsync(repository.StoredPath(old), old, cancellationToken);
                    files.Add(old);
                }
                else
                {
                    var entry = new SnapshotFile(file.RelativePath, length, hash, id);
                    await FileIntegrity.CopyVerifiedAsync(input, PathSafety.Resolve(Path.Combine(staging, "files"), file.RelativePath),
                        entry, cancellationToken);
                    files.Add(entry);
                    copied++;
                }
                progress?.Report(new BackupProgress(file.RelativePath, files.Count, copied));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = new SnapshotManifest
            {
                Id = id, CreatedAtUtc = DateTimeOffset.UtcNow, Complete = true,
                Sources = plan.Sources.ToArray(), Directories = scan.Directories.ToArray(), Files = files.ToArray()
            };
            await using (var output = new FileStream(Path.Combine(staging, "manifest.json"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, manifest, SnapshotRepository.JsonOptions, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Same-volume directory rename is the publication boundary. Staging is never restorable.
            Directory.Move(staging, repository.SnapshotDirectory(id));
            return new BackupResult(manifest, copied, files.Count - copied);
        }
        finally
        {
            SnapshotRepository.DeleteStaging(staging);
        }
    }

    public async Task RestoreAsync(string repository, string snapshotId, string destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotRepository.ValidateSnapshotId(snapshotId);
        repository = Path.GetFullPath(repository);
        destination = Path.GetFullPath(destination);
        if (PathSafety.Overlaps(repository, destination))
            throw new ArgumentException("Restore destination and backup repository must not overlap.");
        PathSafety.EnsureNoLinks(destination);
        if (File.Exists(destination) || (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()))
            throw new IOException("Restore destination must be a new or empty directory.");
        using var store = new SnapshotRepository(repository, create: false);
        var manifest = await store.ReadAsync(snapshotId, cancellationToken);
        // Validate all content before creating the destination tree.
        foreach (var file in manifest.Files)
            await FileIntegrity.VerifyAsync(store.StoredPath(file), file, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach (var directory in manifest.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathSafety.Resolve(destination, directory);
            PathSafety.EnsureNoLinks(path);
            Directory.CreateDirectory(path);
        }
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathSafety.Resolve(destination, file.RelativePath);
            PathSafety.EnsureNoLinks(path);
            var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".snapsafe-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var input = FileIntegrity.OpenRead(store.StoredPath(file)))
                    await FileIntegrity.CopyVerifiedAsync(input, temporary, file, cancellationToken);
                PathSafety.EnsureNoLinks(path);
                File.Move(temporary, path, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }
}
