namespace SnapSafe.Core;

internal sealed record RestoreSelection(string[] Directories, SnapshotFile[] Files)
{
    internal static RestoreSelection Create(SnapshotManifest manifest, IReadOnlyList<string> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var known = manifest.Directories.Concat(manifest.Files.Select(file => file.RelativePath)).ToArray();
        var paths = requested.Select(path =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            path = path.Replace('\\', '/').TrimEnd('/');
            _ = PathSafety.Resolve(Path.GetTempPath(), path);
            return known.FirstOrDefault(candidate => string.Equals(candidate, path, comparison))
                ?? throw new ArgumentException($"Path does not exist in the snapshot: {path}");
        }).Distinct(StringComparer.Ordinal).ToArray();
        bool Under(string path, string parent) => string.Equals(path, parent, comparison) || path.StartsWith(parent + "/", comparison);
        return new RestoreSelection(
            manifest.Directories.Where(directory => paths.Length == 0 || paths.Any(path => Under(directory, path) || Under(path, directory))).ToArray(),
            manifest.Files.Where(file => paths.Length == 0 || paths.Any(path => Under(file.RelativePath, path))).ToArray());
    }
}
