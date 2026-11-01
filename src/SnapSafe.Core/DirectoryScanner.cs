namespace SnapSafe.Core;

public sealed record ScannedFile(string RelativePath, string FullPath);
public sealed record DirectoryScan(IReadOnlyList<string> Directories, IReadOnlyList<ScannedFile> Files);

public static class DirectoryScanner
{
    /// <summary>Includes hidden files and empty directories; fails explicitly on links or inaccessible entries.</summary>
    public static DirectoryScan Scan(BackupPlan plan, CancellationToken cancellationToken = default)
    {
        plan = plan.ValidateForBackup();
        var directories = new List<string>();
        var files = new List<ScannedFile>();
        foreach (var source in plan.Sources)
        {
            var pending = new Stack<string>();
            pending.Push(source.Directory);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PathSafety.EnsureNoLinks(directory);
                var relative = Path.GetRelativePath(source.Directory, directory);
                directories.Add(relative == "." ? source.Name : source.Name + "/" + relative.Replace('\\', '/'));
                foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PathSafety.ValidateSegment(Path.GetFileName(entry));
                    PathSafety.EnsureNoLinks(entry);
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                        pending.Push(entry);
                    else
                    {
                        relative = source.Name + "/" + Path.GetRelativePath(source.Directory, entry).Replace('\\', '/');
                        _ = PathSafety.Resolve(plan.DestinationDirectory, relative);
                        files.Add(new ScannedFile(relative, entry));
                    }
                }
            }
        }
        return new DirectoryScan(directories.Order(StringComparer.Ordinal).ToArray(),
            files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray());
    }
}
