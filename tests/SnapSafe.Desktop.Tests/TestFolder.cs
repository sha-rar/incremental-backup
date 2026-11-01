namespace SnapSafe.Desktop.Tests;

public sealed class TestFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "SnapSafe.Desktop.Tests", Guid.NewGuid().ToString("N"));
    public TestFolder() => Directory.CreateDirectory(Root);
    public string At(string relative) => Path.GetFullPath(Path.Combine(Root, relative));
    public string Write(string relative, string contents)
    {
        var path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
    public void Dispose() => Directory.Delete(Root, true);
}
