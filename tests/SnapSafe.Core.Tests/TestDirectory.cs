namespace SnapSafe.Core.Tests;

public sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SnapSafe.Tests", Guid.NewGuid().ToString("N"));

    public TestDirectory() => Directory.CreateDirectory(Root);
    public string PathFor(string relative) => Path.GetFullPath(Path.Combine(Root, relative));
    public string CreateDirectory(string relative)
    {
        var path = PathFor(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string Write(string relative, string contents)
    {
        var path = PathFor(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
