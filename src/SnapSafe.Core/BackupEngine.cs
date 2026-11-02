using System.Text.Json;

namespace SnapSafe.Core;

public sealed class BackupEngine
{
    private readonly IDestinationProbe destinationProbe;
    public BackupEngine(IDestinationProbe? destinationProbe = null) => this.destinationProbe = destinationProbe ?? new DestinationProbe();
    public Task<RetentionResult> ApplyRetentionAsync(string repository, RetentionPolicy policy,
        DateTimeOffset? now = null, CancellationToken cancellationToken = default) =>
        SnapshotRetention.ApplyAsync(repository, policy, now ?? DateTimeOffset.UtcNow, cancellationToken);

    public Task<RestoreResult> RestoreAsync(string repository, string snapshotId, string destination,
        RestoreOptions options, CancellationToken cancellationToken = default) =>
        SnapshotRestorer.RestoreAsync(repository, snapshotId, destination, options, cancellationToken);

    public async Task<IReadOnlyList<SnapshotSummary>> ListSnapshotsAsync(string repository, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new SnapshotRepository(PathSafety.DirectoryPath(repository), create: false, readOnly: true);
        var snapshots = await store.ListAsync(cancellationToken);
        return Array.AsReadOnly(snapshots.Select(snapshot => new SnapshotSummary(snapshot.Id, snapshot.CreatedAtUtc,
            snapshot.Files.Length, snapshot.Files.Sum(file => file.Length), snapshot.Complete)).ToArray());
    }

    public async Task<SnapshotManifest> GetSnapshotAsync(string repository, string snapshotId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new SnapshotRepository(PathSafety.DirectoryPath(repository), create: false, readOnly: true);
        return await store.ReadAsync(snapshotId, cancellationToken);
    }

    public async Task<BackupResult> CreateSnapshotAsync(BackupPlan plan, bool fullBackup = false,
        IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        plan = plan.ValidateForBackup();
        destinationProbe.EnsureAccessible(plan.DestinationDirectory);
        destinationProbe.EnsureCapacity(plan.DestinationDirectory, 4096);
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
            long sourceBytes = 0;
            var manifestReserve = checked(4096L + scan.Files.Count * 512L + scan.Directories.Count * 128L);
            foreach (var file in scan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var input = FileIntegrity.OpenRead(file.FullPath);
                var length = input.Length;
                previousFiles.TryGetValue(file.RelativePath, out var old);
                string? hash = null;
                if (old is not null && old.Length == length)
                {
                    hash = await FileIntegrity.HashAsync(input, cancellationToken); sourceBytes += length;
                }
                if (old is not null && hash is not null && string.Equals(old.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                {
                    // Do not propagate a damaged previous copy into another completed snapshot.
                    await FileIntegrity.VerifyAsync(await repository.StoredPathAsync(old, cancellationToken), old, cancellationToken);
                    files.Add(old);
                }
                else
                {
                    destinationProbe.EnsureCapacity(plan.DestinationDirectory, checked(length + manifestReserve));
                    var entry = await FileIntegrity.CopyAndHashAsync(input, PathSafety.Resolve(Path.Combine(staging, "files"), file.RelativePath),
                        file.RelativePath, id, cancellationToken);
                    sourceBytes += length;
                    if (hash is not null && entry.Sha256 != hash) throw new IOException($"Source changed during backup: {file.RelativePath}");
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
            destinationProbe.EnsureCapacity(plan.DestinationDirectory, manifestReserve);
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
            return new BackupResult(manifest, copied, files.Count - copied) { SourceBytesRead = sourceBytes };
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 112 or 39)
        { throw new BackupDestinationException("Backup stopped because the destination ran out of space. Free space and retry; completed snapshots were kept.", exception); }
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
