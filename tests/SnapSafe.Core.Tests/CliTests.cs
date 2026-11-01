using SnapSafe.Cli;
using System.Text.Json;
using Xunit;

namespace SnapSafe.Core.Tests;

public class CliTests
{
    private static async Task<(int Code, string Output, string Error)> Run(string[] args, CancellationToken token = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliApplication.RunAsync(args, output, error, token);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task CliBacksUpIncrementallyForcesFullCopyAndRestoresOldSnapshot()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "first");
        var config = temp.Write("job.json", JsonSerializer.Serialize(new
        {
            sources = new[] { new { name = "docs", directory = "input" } }, destinationDirectory = "backup"
        }));
        var first = await Run(["backup", "--config", config]);
        Assert.Equal(0, first.Code);
        Assert.Empty(first.Error);
        Assert.Contains("1 copied", first.Output);
        var id = Directory.GetDirectories(temp.PathFor("backup/snapshots")).Select(Path.GetFileName).Single()!;
        var second = await Run(["backup", "--config", config]);
        Assert.Equal(0, second.Code);
        Assert.Contains("1 reused", second.Output);
        var full = await Run(["backup", "--full", "--config", config]);
        Assert.Equal(0, full.Code);
        Assert.Contains("1 copied", full.Output);
        temp.Write("input/file", "second");
        var restored = await Run(["restore", "--repository", temp.PathFor("backup"), "--snapshot", id,
            "--destination", temp.PathFor("restore")]);
        Assert.Equal(0, restored.Code);
        Assert.Empty(restored.Error);
        Assert.Equal("first", await File.ReadAllTextAsync(temp.PathFor("restore/docs/file")));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpDocumentsBothCommands(string flag)
    {
        var result = await Run([flag]);
        Assert.Equal(0, result.Code);
        Assert.Contains("backup --config", result.Output);
        Assert.Contains("restore --repository", result.Output);
        Assert.Empty(result.Error);
    }

    public static TheoryData<string[]> InvalidCommands => new()
    {
        new string[] { "unknown" },
        new string[] { "backup" },
        new string[] { "backup", "--config" },
        new string[] { "backup", "--config", "a", "--config", "b" },
        new string[] { "backup", "--config", "a", "--typo" },
        new string[] { "restore", "--repository", "repo", "--snapshot", "id" },
        new string[] { "restore", "--full", "--repository", "repo", "--snapshot", "id", "--destination", "out" }
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task InvalidCommandsHaveUsageExitCode(string[] args)
    {
        var result = await Run(args);
        Assert.Equal(2, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("System.", result.Error);
    }

    [Fact]
    public async Task MissingOrMalformedConfigurationHasOperationalExitCode()
    {
        using var temp = new TestDirectory();
        var missing = await Run(["backup", "--config", temp.PathFor("missing.json")]);
        Assert.Equal(1, missing.Code);
        var malformed = temp.Write("job.json", "{broken");
        var result = await Run(["backup", "--config", malformed]);
        Assert.Equal(1, result.Code);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public async Task CancellationHasDistinctExitCodeAndNoCompletedSnapshot()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var config = temp.Write("job.json", """
            {"sources":[{"name":"docs","directory":"input"}],"destinationDirectory":"backup"}
            """);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await Run(["backup", "--config", config], cancellation.Token);
        Assert.Equal(130, result.Code);
        Assert.Contains("cancel", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(temp.PathFor("backup/snapshots")));
    }
}
