namespace SnapSafe.Core;

internal static class PathSafety
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static void ValidateSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0 ||
            value.Any(char.IsControl) || value.EndsWith('.') || value.EndsWith(' '))
            throw new ArgumentException($"Invalid path component: {value}");
        var stem = value.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Reserved path component: {value}");
    }

    internal static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException("A relative path is required.");
        foreach (var segment in relative.Replace('\\', '/').Split('/'))
            ValidateSegment(segment);
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    internal static bool Overlaps(string first, string second) => Contains(first, second) || Contains(second, first);

    private static bool Contains(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        return string.Equals(parent, child, Comparison) || child.StartsWith(Path.EndsInDirectorySeparator(parent)
            ? parent : parent + Path.DirectorySeparatorChar, Comparison);
    }

    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Filesystem links are not supported: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
