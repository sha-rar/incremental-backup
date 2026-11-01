using SnapSafe.Cli;
using System.Text.Json;
using Xunit;

namespace SnapSafe.Core.Tests;

public class SnapshotCliTests
{
    private static async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliApplication.RunAsync(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task CliLifecycleBrowsesVersionsAndRestoresDeletedFileWithConflictPolicies()
    {
        using var temp = new TestDirectory();
        var original = temp.Write("input/nested/report.txt", "version one");
        temp.Write("input/shared.txt", "unchanged");
        temp.Write("input/draft.tmp", "excluded");
        temp.CreateDirectory("input/nested/empty");
        var config = temp.Write("job.json", """
            {"sources":[{"name":"docs","directory":"input"}],"destinationDirectory":"backup","exclusions":["*.tmp"]}
            """);
        Assert.Equal(0, (await Run("backup", "--config", config)).Code);
        var repository = temp.PathFor("backup");
        var engine = new BackupEngine();
        var first = Assert.Single(await engine.ListSnapshotsAsync(repository));
        await File.WriteAllTextAsync(original, "version two");
        Assert.Equal(0, (await Run("backup", "--config", config)).Code);
        File.Delete(original);
        Assert.Equal(0, (await Run("backup", "--config", config)).Code);
        var history = await Run("list", "--repository", repository);
        Assert.Equal(0, history.Code);
        Assert.Empty(history.Error);
        var snapshots = await engine.ListSnapshotsAsync(repository);
        Assert.Equal(3, snapshots.Count);
        Assert.All(snapshots, snapshot => Assert.Contains(snapshot.Id, history.Output));
        var inspected = await Run("inspect", "--repository", repository, "--snapshot", first.Id);
        Assert.Equal(0, inspected.Code);
        using var metadata = JsonDocument.Parse(inspected.Output);
        Assert.True(metadata.RootElement.GetProperty("Complete").GetBoolean());
        Assert.Equal(2, metadata.RootElement.GetProperty("Files").GetArrayLength());
        Assert.DoesNotContain("docs/draft.tmp", inspected.Output);
        var destination = temp.PathFor("restore");
        temp.Write("restore/docs/nested/report.txt", "user edits");
        var baseArgs = new[] { "restore", "--repository", repository, "--snapshot", first.Id, "--destination", destination,
            "--path", "docs/nested/report.txt", "--path", "docs/nested/empty" };
        Assert.Equal(1, (await Run(baseArgs)).Code);
        var skipped = await Run([.. baseArgs, "--on-conflict", "skip"]);
        Assert.Equal(0, skipped.Code);
        Assert.Contains("1 skipped", skipped.Output);
        Assert.Equal("user edits", await File.ReadAllTextAsync(temp.PathFor("restore/docs/nested/report.txt")));
        var renamed = await Run([.. baseArgs, "--on-conflict", "rename"]);
        Assert.Equal(0, renamed.Code);
        Assert.Contains("report (restored 1).txt", renamed.Output);
        Assert.Equal("version one", await File.ReadAllTextAsync(temp.PathFor("restore/docs/nested/report (restored 1).txt")));
        var overwritten = await Run([.. baseArgs, "--on-conflict", "overwrite"]);
        Assert.Equal(0, overwritten.Code);
        Assert.Equal("version one", await File.ReadAllTextAsync(temp.PathFor("restore/docs/nested/report.txt")));
        Assert.True(Directory.Exists(temp.PathFor("restore/docs/nested/empty")));
        Assert.False(File.Exists(temp.PathFor("restore/docs/shared.txt")));
        Assert.DoesNotContain((await engine.GetSnapshotAsync(repository, snapshots[0].Id)).Files, file => file.RelativePath == "docs/nested/report.txt");
        var originalMetadata = await engine.GetSnapshotAsync(repository, first.Id);
        Assert.Equal("version one", await File.ReadAllTextAsync(temp.PathFor($"backup/snapshots/{first.Id}/files/docs/nested/report.txt")));
        Assert.Equal(2, originalMetadata.Files.Length);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("inspect")]
    public async Task NewCommandsSupportHelp(string command)
    {
        var result = await Run(command, "--help");
        Assert.Equal(0, result.Code);
        Assert.Contains("list --repository", result.Output);
        Assert.Contains("inspect --repository", result.Output);
        Assert.Contains("--on-conflict", result.Output);
    }

    [Theory]
    [InlineData("erase")]
    [InlineData("42")]
    [InlineData("-1")]
    public async Task InvalidConflictPoliciesAreUsageErrors(string policy)
    {
        var result = await Run("restore", "--repository", "missing", "--snapshot", Guid.NewGuid().ToString("N"),
            "--destination", "out", "--on-conflict", policy);
        Assert.Equal(2, result.Code);
        Assert.Contains("conflict", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
