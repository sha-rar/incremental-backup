using System.Text.Json;

namespace SnapSafe.Core;

public sealed class BackupEngine
{
    public Task<RestoreResult> RestoreAsync(string repository, string snapshotId, string destination,
        RestoreOptions options, CancellationToken cancellationToken = default) =>
        SnapshotRestorer.RestoreAsync(repository, snapshotId, destination, options, cancellationToken);

    public async Task<IReadOnlyList<SnapshotSummary>> ListSnapshotsAsync(string repository, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new SnapshotRepository(PathSafety.DirectoryPath(repository), create: false);
        var snapshots = await store.ListAsync(cancellationToken);
        return Array.AsReadOnly(snapshots.Select(snapshot => new SnapshotSummary(snapshot.Id, snapshot.CreatedAtUtc,
            snapshot.Files.Length, snapshot.Files.Sum(file => file.Length), snapshot.Complete)).ToArray());
    }

    public async Task<SnapshotManifest> GetSnapshotAsync(string repository, string snapshotId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new SnapshotRepository(PathSafety.DirectoryPath(repository), create: false);
        return await store.ReadAsync(snapshotId, cancellationToken);
    }

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
                Sources = plan.Sources.ToArray(), Directories = scan.Directories.ToArray(), Files = files.ToArray(),
                Exclusions = plan.Exclusions.ToArray()
            };
            SnapshotRepository.ValidateManifest(manifest, id);
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
        // Retain the Phase 1 overload's empty-target contract. Options enable explicit merging.
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotRepository.ValidateSnapshotId(snapshotId);
        repository = Path.GetFullPath(repository);
        destination = Path.GetFullPath(destination);
        if (PathSafety.Overlaps(repository, destination))
            throw new ArgumentException("Restore destination and backup repository must not overlap.");
        PathSafety.EnsureNoLinks(destination);
        if (File.Exists(destination) || (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()))
            throw new IOException("Restore destination must be a new or empty directory.");
        await RestoreAsync(repository, snapshotId, destination, new RestoreOptions(), cancellationToken);
    }
}
