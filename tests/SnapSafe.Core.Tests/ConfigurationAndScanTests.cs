using System.Text.Json;
using Xunit;

namespace SnapSafe.Core.Tests;

public class ConfigurationAndScanTests
{
    [Fact]
    public async Task ConfigurationResolvesPathsRelativeToItsFile()
    {
        using var temp = new TestDirectory();
        var file = temp.Write("config/job.json", JsonSerializer.Serialize(new
        {
            sources = new[] { new { name = "docs", directory = "../documents" }, new { name = "photos", directory = "photos" } },
            destinationDirectory = "../backup"
        }));
        var plan = await BackupPlan.LoadAsync(file);
        Assert.Equal(temp.PathFor("documents"), plan.Sources[0].Directory);
        Assert.Equal(temp.PathFor("config/photos"), plan.Sources[1].Directory);
        Assert.Equal(temp.PathFor("backup"), plan.DestinationDirectory);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("C:")]
    [InlineData("NUL")]
    [InlineData("")]
    public void InvalidSourceNamesAreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => new BackupPlan([new BackupSource(name, "source")], "backup"));

    [Fact]
    public void MissingAndDuplicateSourcesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new BackupPlan([], "backup"));
        Assert.Throws<ArgumentException>(() => new BackupPlan([
            new BackupSource("Docs", "one"), new BackupSource("docs", "two")], "backup"));
    }

    [Fact]
    public void ScanPreservesMultipleRootsNestedPathsAndEmptyDirectories()
    {
        using var temp = new TestDirectory();
        temp.CreateDirectory("one/nested/empty");
        temp.Write("one/nested/data.txt", "hello");
        temp.Write("two/data.txt", "world");
        var scan = DirectoryScanner.Scan(new BackupPlan([
            new BackupSource("docs", temp.PathFor("one")), new BackupSource("photos", temp.PathFor("two"))], temp.PathFor("backup")));
        Assert.Equal(["docs", "docs/nested", "docs/nested/empty", "photos"], scan.Directories);
        Assert.Equal(["docs/nested/data.txt", "photos/data.txt"], scan.Files.Select(f => f.RelativePath));
        Assert.All(scan.Files, f => Assert.True(File.Exists(f.FullPath)));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("source/backup")]
    [InlineData(".")]
    public void ScanRejectsOverlappingRepository(string destination)
    {
        using var temp = new TestDirectory();
        var source = temp.CreateDirectory("source");
        Assert.Throws<ArgumentException>(() => DirectoryScanner.Scan(new BackupPlan(source, temp.PathFor(destination))));
    }

    [Fact]
    public void ScanRejectsMissingSource()
    {
        using var temp = new TestDirectory();
        Assert.Throws<DirectoryNotFoundException>(() => DirectoryScanner.Scan(
            new BackupPlan(temp.PathFor("missing"), temp.PathFor("backup"))));
    }

    [Fact]
    public void ExtendedWindowsPathAliasCannotBypassSourceOverlapCheck()
    {
        using var temp = new TestDirectory();
        var source = temp.CreateDirectory("input");
        var destination = (OperatingSystem.IsWindows() ? @"\\?\" : "") + Path.Combine(source, "backup");
        Assert.Throws<ArgumentException>(() => DirectoryScanner.Scan(new BackupPlan(source, destination)));
    }

    [Fact]
    public void WindowsDeviceNamespaceIsRejectedBeforeBackupScanning()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var temp = new TestDirectory();
        var source = temp.CreateDirectory("input");
        var destination = @"\\.\" + Path.Combine(source, "backup");
        Assert.Throws<ArgumentException>(() => DirectoryScanner.Scan(new BackupPlan(source, destination)));
        Assert.False(Directory.Exists(Path.Combine(source, "backup")));
    }
}
