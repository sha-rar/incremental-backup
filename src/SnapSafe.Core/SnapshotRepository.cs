using System.Text.Json;

namespace SnapSafe.Core;

internal sealed class SnapshotRepository : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly FileStream? repositoryLock;
    private readonly Dictionary<string, Dictionary<string, SnapshotFile>> ownedFiles = new(StringComparer.Ordinal);
    internal string SnapshotsDirectory { get; }
    internal string RetainedDataDirectory { get; }

    internal SnapshotRepository(string directory, bool create, bool readOnly = false)
    {
        directory = Path.GetFullPath(directory);
        PathSafety.EnsureNoLinks(directory);
        SnapshotsDirectory = Path.Combine(directory, "snapshots");
        RetainedDataDirectory = Path.Combine(directory, "retained-data");
        PathSafety.EnsureNoLinks(RetainedDataDirectory);
        PathSafety.EnsureNoLinks(SnapshotsDirectory);
        var lockPath = Path.Combine(directory, ".lock");
        PathSafety.EnsureNoLinks(lockPath);
        if (create)
            Directory.CreateDirectory(directory);
        else if (!Directory.Exists(SnapshotsDirectory))
            throw new DirectoryNotFoundException($"Backup repository does not exist: {directory}");
        // Readers share the lock with each other but not with writers, so recovery works from read-only media.
        // A repository without a lock file has never been written by a backup, so readers do not create one.
        if (readOnly)
            repositoryLock = File.Exists(lockPath) ? new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        else
            repositoryLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (create)
                Directory.CreateDirectory(SnapshotsDirectory);
        }
        catch
        {
            repositoryLock?.Dispose();
            throw;
        }
    }

    internal static void ValidateSnapshotId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Snapshot ID must be a 32-character identifier produced by backup.", nameof(id));
    }

    internal string SnapshotDirectory(string id)
    {
        ValidateSnapshotId(id);
        var path = Path.Combine(SnapshotsDirectory, id);
        PathSafety.EnsureNoLinks(path);
        return path;
    }

    internal async Task<string> StoredPathAsync(SnapshotFile file, CancellationToken cancellationToken)
    {
        var directory = SnapshotDirectory(file.StorageSnapshotId);
        if (!Directory.Exists(directory)) directory = RetainedDirectory(file.StorageSnapshotId);
        if (!ownedFiles.TryGetValue(file.StorageSnapshotId, out var files))
        {
            var manifest = await ReadManifestAtAsync(directory, file.StorageSnapshotId, cancellationToken);
            files = manifest.Files.Where(entry => entry.StorageSnapshotId == manifest.Id)
                .ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
            ownedFiles.Add(file.StorageSnapshotId, files);
        }
        if (!files.TryGetValue(file.RelativePath, out var stored) || stored.Length != file.Length ||
            !string.Equals(stored.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Referenced data does not match its owning snapshot: {file.RelativePath}");
        var path = PathSafety.Resolve(Path.Combine(directory, "files"), file.RelativePath);
        PathSafety.EnsureNoLinks(path);
        return path;
    }

    internal async Task<SnapshotManifest> ReadAsync(string id, CancellationToken cancellationToken, bool requireComplete = true)
        => await ReadManifestAtAsync(SnapshotDirectory(id), id, cancellationToken, requireComplete);

    internal string RetainedDirectory(string id)
    {
        ValidateSnapshotId(id);
        var path = Path.Combine(RetainedDataDirectory, id);
        PathSafety.EnsureNoLinks(path);
        return path;
    }

    internal async Task<SnapshotManifest> ReadManifestAtAsync(string directory, string id, CancellationToken cancellationToken, bool requireComplete = true)
    {
        var path = Path.Combine(directory, "manifest.json");
        PathSafety.EnsureNoLinks(path);
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Empty snapshot manifest.");
        ValidateManifest(manifest, id, requireComplete);
        return manifest;
    }

    internal async Task<SnapshotManifest?> LatestAsync(CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken)).FirstOrDefault();

    internal async Task<IReadOnlyList<SnapshotManifest>> ListAsync(CancellationToken cancellationToken)
    {
        var snapshots = new List<SnapshotManifest>();
        foreach (var directory in Directory.EnumerateDirectories(SnapshotsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _))
                continue;
            var manifest = await ReadAsync(id, cancellationToken, requireComplete: false);
            if (manifest.Complete)
                snapshots.Add(manifest);
        }
        return snapshots.OrderByDescending(snapshot => snapshot.CreatedAtUtc)
            .ThenByDescending(snapshot => snapshot.Id, StringComparer.Ordinal).ToArray();
    }

    internal void RemoveStaleStaging(CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(SnapshotsDirectory, ".pending-*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (Guid.TryParseExact(name[9..], "N", out _))
                DeleteStaging(directory);
        }
    }

    internal static void DeleteStaging(string path)
    {
        if (!Directory.Exists(path))
            return;
        // Never traverse a replaced staging directory or a link inside it.
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            PathSafety.EnsureNoLinks(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                PathSafety.EnsureNoLinks(entry);
                if (Directory.Exists(entry))
                    pending.Push(entry);
            }
        }
        Directory.Delete(path, recursive: true);
    }

    internal static void ValidateManifest(SnapshotManifest manifest, string id, bool requireComplete = true)
    {
        if (manifest.FormatVersion != 1 || manifest.Id != id || (requireComplete && !manifest.Complete) ||
            manifest.Sources is null || manifest.Sources.Length == 0 || manifest.Directories is null || manifest.Files is null)
            throw new InvalidDataException("Unsupported or incomplete snapshot manifest.");
        if (manifest.Exclusions is null)
            throw new InvalidDataException("Invalid snapshot exclusions.");
        foreach (var pattern in manifest.Exclusions)
            _ = ExclusionMatcher.Normalize(pattern);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in manifest.Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Directory))
                throw new InvalidDataException("Invalid snapshot source.");
            PathSafety.ValidateSegment(source.Name);
            if (!roots.Add(source.Name))
                throw new InvalidDataException("Duplicate snapshot source.");
        }
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in manifest.Directories)
        {
            ValidateRelativePath(directory, roots);
            if (!directories.Add(directory))
                throw new InvalidDataException("Duplicate snapshot directory.");
        }
        if (roots.Any(root => !directories.Contains(root)))
            throw new InvalidDataException("Snapshot is missing a source directory.");
        foreach (var directory in directories.Where(d => d.Contains('/')))
        {
            if (!directories.Contains(directory[..directory.LastIndexOf('/')]))
                throw new InvalidDataException("Snapshot is missing a parent directory.");
        }
        var paths = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null)
                throw new InvalidDataException("Null snapshot file.");
            ValidateRelativePath(file.RelativePath, roots);
            ValidateSnapshotId(file.StorageSnapshotId);
            var slash = file.RelativePath.LastIndexOf('/');
            if (slash < 0 || !directories.Contains(file.RelativePath[..slash]) || !paths.Add(file.RelativePath) ||
                file.Length < 0 || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid snapshot file metadata.");
        }
    }

    private static void ValidateRelativePath(string path, HashSet<string> roots)
    {
        if (path is null || path.Contains('\\'))
            throw new InvalidDataException("Snapshot paths must use forward slashes.");
        _ = PathSafety.Resolve(Path.GetTempPath(), path);
        if (!roots.Contains(path.Split('/')[0]))
            throw new InvalidDataException("Snapshot path is outside its named sources.");
    }

    public void Dispose() => repositoryLock?.Dispose();
}
