using System.Text.Json;
using Xunit;

namespace SnapSafe.Core.Tests;

public class ExclusionTests
{
    private static Task<BackupPlan> Load(TestDirectory temp, string[] exclusions)
    {
        var path = temp.Write("job.json", JsonSerializer.Serialize(new
        {
            sources = new[] { new { name = "docs", directory = "input" } },
            destinationDirectory = "backup", exclusions
        }));
        return BackupPlan.LoadAsync(path);
    }

    [Fact]
    public async Task ExclusionsPruneDirectoriesAndMatchFilesAtAnyDepth()
    {
        using var temp = new TestDirectory();
        temp.Write("input/keep.txt", "keep");
        temp.Write("input/root.tmp", "temporary");
        temp.Write("input/nested/also.tmp", "temporary");
        temp.Write("input/nested/keep.txt", "keep");
        temp.Write("input/bin/output.dll", "generated");
        temp.Write("input/nested/bin/output.dll", "generated");
        temp.Write("input/private/secret", "secret");
        var scan = DirectoryScanner.Scan(await Load(temp, ["**/*.tmp", "**/bin/**", "docs/private"]));
        Assert.Equal(["docs", "docs/nested"], scan.Directories);
        Assert.Equal(["docs/keep.txt", "docs/nested/keep.txt"], scan.Files.Select(f => f.RelativePath));
    }

    [Theory]
    [InlineData("*.tmp")]
    [InlineData("**/*.tmp")]
    [InlineData("docs/**/*.tmp")]
    [InlineData("docs/?ile.tmp")]
    public async Task WildcardsMatchRootAndNestedFilesAsSpecified(string pattern)
    {
        using var temp = new TestDirectory();
        temp.Write("input/file.tmp", "temp");
        temp.Write("input/keep.txt", "keep");
        var scan = DirectoryScanner.Scan(await Load(temp, [pattern]));
        Assert.Equal("docs/keep.txt", Assert.Single(scan.Files).RelativePath);
    }

    [Fact]
    public async Task QualifiedExclusionsDoNotAffectOtherSourcesOrSimilarlyNamedDirectories()
    {
        using var temp = new TestDirectory();
        temp.Write("input/cache/file", "exclude");
        temp.Write("input/cacheable/file", "keep");
        temp.Write("other/cache/file", "keep");
        var path = temp.Write("job.json", """
            {"sources":[{"name":"docs","directory":"input"},{"name":"other","directory":"other"}],
             "destinationDirectory":"backup","exclusions":["docs/cache/"]}
            """);
        var scan = DirectoryScanner.Scan(await BackupPlan.LoadAsync(path));
        Assert.Equal(["docs/cacheable/file", "other/cache/file"], scan.Files.Select(f => f.RelativePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("docs//cache")]
    [InlineData("C:/files")]
    [InlineData("docs/**oops")]
    public async Task InvalidExclusionPatternsAreRejected(string pattern)
    {
        using var temp = new TestDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() => Load(temp, [pattern]));
    }

    [Fact]
    public async Task ExcludingPreviouslyBackedUpFilePreservesItsOlderVersion()
    {
        using var temp = new TestDirectory();
        temp.Write("input/secret", "recoverable");
        var engine = new BackupEngine();
        var first = await engine.CreateSnapshotAsync(await Load(temp, []));
        var second = await engine.CreateSnapshotAsync(await Load(temp, ["secret"]));
        Assert.Empty(second.Snapshot.Files);
        Assert.Equal(["docs"], second.Snapshot.Directories);
        await engine.RestoreAsync(temp.PathFor("backup"), first.Snapshot.Id, temp.PathFor("restore"));
        Assert.Equal("recoverable", await File.ReadAllTextAsync(temp.PathFor("restore/docs/secret")));
    }

    [Fact]
    public void InvalidDestinationIsRejectedBeforeScanning()
    {
        using var temp = new TestDirectory();
        var source = temp.CreateDirectory("input");
        Assert.Throws<ArgumentException>(() => DirectoryScanner.Scan(new BackupPlan(source, Path.Combine(temp.Root, "bad|repo"))));
    }
}
