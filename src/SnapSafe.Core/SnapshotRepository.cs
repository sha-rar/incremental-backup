using System.Text.Json;

namespace SnapSafe.Core;

internal sealed class SnapshotRepository : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly FileStream repositoryLock;
    internal string SnapshotsDirectory { get; }

    internal SnapshotRepository(string directory, bool create)
    {
        directory = Path.GetFullPath(directory);
        PathSafety.EnsureNoLinks(directory);
        SnapshotsDirectory = Path.Combine(directory, "snapshots");
        PathSafety.EnsureNoLinks(SnapshotsDirectory);
        var lockPath = Path.Combine(directory, ".lock");
        PathSafety.EnsureNoLinks(lockPath);
        if (create)
            Directory.CreateDirectory(directory);
        else if (!Directory.Exists(SnapshotsDirectory))
            throw new DirectoryNotFoundException($"Backup repository does not exist: {directory}");
        repositoryLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (create)
                Directory.CreateDirectory(SnapshotsDirectory);
        }
        catch
        {
            repositoryLock.Dispose();
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

    internal string StoredPath(SnapshotFile file)
    {
        var directory = SnapshotDirectory(file.StorageSnapshotId);
        var manifest = Path.Combine(directory, "manifest.json");
        PathSafety.EnsureNoLinks(manifest);
        if (!File.Exists(manifest))
            throw new InvalidDataException($"Referenced snapshot is missing: {file.StorageSnapshotId}");
        var path = PathSafety.Resolve(Path.Combine(directory, "files"), file.RelativePath);
        PathSafety.EnsureNoLinks(path);
        return path;
    }

    internal async Task<SnapshotManifest> ReadAsync(string id, CancellationToken cancellationToken)
    {
        var path = Path.Combine(SnapshotDirectory(id), "manifest.json");
        PathSafety.EnsureNoLinks(path);
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Empty snapshot manifest.");
        ValidateManifest(manifest, id);
        return manifest;
    }

    internal async Task<SnapshotManifest?> LatestAsync(CancellationToken cancellationToken)
    {
        SnapshotManifest? latest = null;
        foreach (var directory in Directory.EnumerateDirectories(SnapshotsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _))
                continue;
            var manifest = await ReadAsync(id, cancellationToken);
            if (latest is null || manifest.CreatedAtUtc > latest.CreatedAtUtc ||
                (manifest.CreatedAtUtc == latest.CreatedAtUtc && string.CompareOrdinal(manifest.Id, latest.Id) > 0))
                latest = manifest;
        }
        return latest;
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

    internal static void ValidateManifest(SnapshotManifest manifest, string id)
    {
        if (manifest.FormatVersion != 1 || manifest.Id != id || !manifest.Complete ||
            manifest.Sources is null || manifest.Sources.Length == 0 || manifest.Directories is null || manifest.Files is null)
            throw new InvalidDataException("Unsupported or incomplete snapshot manifest.");
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

    public void Dispose() => repositoryLock.Dispose();
}
