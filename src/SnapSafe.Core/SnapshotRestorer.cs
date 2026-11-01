namespace SnapSafe.Core;

internal static class SnapshotRestorer
{
    private sealed record PlannedFile(SnapshotFile File, string RelativePath, string FullPath);

    internal static async Task<RestoreResult> RestoreAsync(string repository, string snapshotId, string destination,
        RestoreOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.ConflictPolicy))
            throw new ArgumentException("Invalid restore conflict policy.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotRepository.ValidateSnapshotId(snapshotId);
        repository = PathSafety.DirectoryPath(repository);
        destination = PathSafety.DirectoryPath(destination);
        if (PathSafety.Overlaps(repository, destination))
            throw new ArgumentException("Restore destination and backup repository must not overlap.");
        PathSafety.EnsureNoLinks(destination);
        if (File.Exists(destination))
            throw new IOException("Restore destination must be a directory.");
        using var store = new SnapshotRepository(repository, create: false);
        var manifest = await store.ReadAsync(snapshotId, cancellationToken);
        var selected = RestoreSelection.Create(manifest, options.Paths);
        var planned = PlanFiles(destination, selected, options.ConflictPolicy, cancellationToken);
        // Check all selected, non-skipped data before altering existing destination contents.
        foreach (var file in planned)
            await FileIntegrity.VerifyAsync(store.StoredPath(file.File), file.File, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach (var directory in selected.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathSafety.Resolve(destination, directory);
            PathSafety.EnsureNoLinks(path);
            Directory.CreateDirectory(path);
        }
        foreach (var file in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.EnsureNoLinks(file.FullPath);
            var temporary = Path.Combine(Path.GetDirectoryName(file.FullPath)!, ".snapsafe-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var input = FileIntegrity.OpenRead(store.StoredPath(file.File)))
                    await FileIntegrity.CopyVerifiedAsync(input, temporary, file.File, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                PathSafety.EnsureNoLinks(file.FullPath);
                File.Move(temporary, file.FullPath, overwrite: options.ConflictPolicy == RestoreConflictPolicy.Overwrite);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        return new RestoreResult(planned.Count, selected.Files.Length - planned.Count,
            Array.AsReadOnly(planned.Where(file => file.RelativePath != file.File.RelativePath)
                .Select(file => new RestoreRename(file.File.RelativePath, file.RelativePath)).ToArray()));
    }

    private static List<PlannedFile> PlanFiles(string destination, RestoreSelection selected,
        RestoreConflictPolicy policy, CancellationToken cancellationToken)
    {
        foreach (var directory in selected.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathSafety.Resolve(destination, directory);
            PathSafety.EnsureNoLinks(path);
            if (File.Exists(path))
                throw new IOException($"Cannot restore directory over an existing file: {directory}");
        }
        var reserved = new HashSet<string>(selected.Directories.Concat(selected.Files.Select(file => file.RelativePath)), StringComparer.OrdinalIgnoreCase);
        var result = new List<PlannedFile>();
        foreach (var file in selected.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = file.RelativePath;
            var path = PathSafety.Resolve(destination, relative);
            PathSafety.EnsureNoLinks(path);
            if (Directory.Exists(path))
                throw new IOException($"Cannot restore file over an existing directory: {relative}");
            if (File.Exists(path))
            {
                if (policy == RestoreConflictPolicy.Fail)
                    throw new IOException($"Restore destination file already exists: {relative}");
                if (policy == RestoreConflictPolicy.Skip)
                    continue;
                if (policy == RestoreConflictPolicy.Rename)
                {
                    var slash = relative.LastIndexOf('/');
                    var parent = relative[..(slash + 1)];
                    var name = relative[(slash + 1)..];
                    var extension = Path.GetExtension(name);
                    var stem = Path.GetFileNameWithoutExtension(name);
                    for (var suffix = 1; ; suffix++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        relative = $"{parent}{stem} (restored {suffix}){extension}";
                        path = PathSafety.Resolve(destination, relative);
                        PathSafety.EnsureNoLinks(path);
                        if (!reserved.Contains(relative) && !File.Exists(path) && !Directory.Exists(path))
                            break;
                    }
                }
            }
            reserved.Add(relative);
            result.Add(new PlannedFile(file, relative, path));
        }
        return result;
    }
}
