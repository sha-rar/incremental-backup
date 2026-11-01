namespace SnapSafe.Core;

internal static class SnapshotRetention
{
    internal static async Task<RetentionResult> ApplyAsync(string repository, RetentionPolicy policy, DateTimeOffset now, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(policy); policy.Validate(); token.ThrowIfCancellationRequested();
        using var store = new SnapshotRepository(PathSafety.DirectoryPath(repository), create: false);
        var snapshots = await store.ListAsync(token);
        if (!policy.Enabled) return new(0, snapshots.Count, 0);
        var survivors = snapshots.Where((snapshot, index) => index == 0 ||
            (index < (policy.MaximumSnapshots ?? int.MaxValue) && (!policy.MaximumAge.HasValue || now - snapshot.CreatedAtUtc <= policy.MaximumAge.Value))).ToArray();
        var kept = survivors.Select(snapshot => snapshot.Id).ToHashSet(StringComparer.Ordinal);
        var references = survivors.SelectMany(snapshot => snapshot.Files).DistinctBy(file => (file.StorageSnapshotId, file.RelativePath, file.Length, file.Sha256)).ToArray();
        // Complete all validation before the first destructive action. Direct owners are never rewritten.
        foreach (var file in references)
            await FileIntegrity.VerifyAsync(await store.StoredPathAsync(file, token), file, token);
        var needed = references.GroupBy(file => file.StorageSnapshotId).ToDictionary(group => group.Key,
            group => group.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        // Preflight every tree that may be mutated, including archives not referenced by survivors.
        foreach (var snapshot in snapshots)
        {
            _ = EnumerateSafeFiles(store.SnapshotDirectory(snapshot.Id), token).Count();
            if (Directory.Exists(store.RetainedDirectory(snapshot.Id))) throw new InvalidDataException("Duplicate active and archived snapshot identifiers.");
        }
        if (Directory.Exists(store.RetainedDataDirectory))
            foreach (var archive in Directory.EnumerateDirectories(store.RetainedDataDirectory))
            {
                _ = EnumerateSafeFiles(archive, token).Count();
                var archiveId = Path.GetFileName(archive);
                if (Guid.TryParseExact(archiveId, "N", out _)) _ = await store.ReadManifestAtAsync(archive, archiveId, token);
            }
        foreach (var trash in Directory.EnumerateDirectories(store.SnapshotsDirectory, ".retention-trash-*"))
            _ = EnumerateSafeFiles(trash, token).Count();
        long reclaimed = 0;
        foreach (var root in new[] { store.SnapshotsDirectory, store.RetainedDataDirectory })
            if (Directory.Exists(root))
                foreach (var trash in Directory.EnumerateDirectories(root, ".retention-trash-*"))
                    if (Guid.TryParseExact(Path.GetFileName(trash)[17..], "N", out _)) reclaimed += RemoveDirectory(trash, token);
        var removed = 0;
        foreach (var snapshot in snapshots.Where(snapshot => !kept.Contains(snapshot.Id)))
        {
            token.ThrowIfCancellationRequested();
            var source = store.SnapshotDirectory(snapshot.Id);
            if (needed.ContainsKey(snapshot.Id))
            {
                Directory.CreateDirectory(store.RetainedDataDirectory);
                Directory.Move(source, store.RetainedDirectory(snapshot.Id));
            }
            else reclaimed += StageAndRemove(source, token);
            removed++;
        }
        if (Directory.Exists(store.RetainedDataDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(store.RetainedDataDirectory))
            {
                token.ThrowIfCancellationRequested();
                var id = Path.GetFileName(directory);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                _ = await store.ReadManifestAtAsync(directory, id, token);
                if (!needed.TryGetValue(id, out var paths)) { reclaimed += StageAndRemove(directory, token); continue; }
                var filesRoot = Path.Combine(directory, "files");
                if (!Directory.Exists(filesRoot)) continue;
                foreach (var file in EnumerateSafeFiles(filesRoot, token))
                {
                    var relative = Path.GetRelativePath(filesRoot, file).Replace('\\', '/');
                    if (paths.Contains(relative)) continue;
                    reclaimed += new FileInfo(file).Length;
                    File.Delete(file);
                }
            }
        }
        return new(removed, survivors.Length, reclaimed);
    }

    private static long StageAndRemove(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Rename first: a crash can leave only explicitly retired, unreferenced deletion staging.
        _ = EnumerateSafeFiles(path, token).Count();
        var trash = Path.Combine(Path.GetDirectoryName(path)!, ".retention-trash-" + Guid.NewGuid().ToString("N"));
        Directory.Move(path, trash);
        return RemoveDirectory(trash, token);
    }
    private static long RemoveDirectory(string path, CancellationToken token)
    {
        var bytes = EnumerateSafeFiles(path, token).Sum(file => new FileInfo(file).Length);
        token.ThrowIfCancellationRequested();
        SnapshotRepository.DeleteStaging(path);
        return bytes;
    }
    private static IEnumerable<string> EnumerateSafeFiles(string root, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested(); PathSafety.EnsureNoLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested(); PathSafety.EnsureNoLinks(entry);
                if (Directory.Exists(entry)) pending.Push(entry); else yield return entry;
            }
        }
    }
}
