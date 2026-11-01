using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace SnapSafe.Core.Tests;

public class SnapshotHistoryTests
{
    private readonly BackupEngine engine = new();

    [Fact]
    public async Task HistoryListsNewestFirstWithSizesAndInspectableContents()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "first");
        var plan = new BackupPlan(temp.PathFor("input"), temp.PathFor("backup"), ["*.tmp"]);
        var first = await engine.CreateSnapshotAsync(plan);
        temp.Write("input/a", "second");
        temp.Write("input/b", "new");
        var second = await engine.CreateSnapshotAsync(plan);
        // Pin ordering independently of clock resolution or wall-clock adjustments.
        await Save(temp, first.Snapshot with { CreatedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z") });
        await Save(temp, second.Snapshot with { CreatedAtUtc = DateTimeOffset.Parse("2026-01-02T00:00:00Z") });
        var history = await engine.ListSnapshotsAsync(plan.DestinationDirectory);
        Assert.Equal([second.Snapshot.Id, first.Snapshot.Id], history.Select(s => s.Id));
        Assert.Equal(2, history[0].FileCount);
        Assert.Equal(9, history[0].TotalBytes);
        Assert.All(history, snapshot => Assert.True(snapshot.Complete));
        var inspected = await engine.GetSnapshotAsync(plan.DestinationDirectory, first.Snapshot.Id);
        var file = Assert.Single(inspected.Files);
        Assert.Equal("source/a", file.RelativePath);
        Assert.Equal(5, file.Length);
        Assert.Equal(first.Snapshot.Files[0].Sha256, file.Sha256);
        Assert.Equal(["*.tmp"], inspected.Exclusions);
    }

    [Fact]
    public async Task StagingAndIncompleteSnapshotsAreHiddenAndCannotBeRestored()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "data");
        var result = await engine.CreateSnapshotAsync(new BackupPlan(temp.PathFor("input"), temp.PathFor("backup")));
        var pending = ".pending-" + Guid.NewGuid().ToString("N");
        temp.Write($"backup/snapshots/{pending}/manifest.json", "{broken");
        var incomplete = result.Snapshot with { Id = Guid.NewGuid().ToString("N"), Complete = false };
        await Save(temp, incomplete);
        Assert.Equal(result.Snapshot.Id, Assert.Single(await engine.ListSnapshotsAsync(temp.PathFor("backup"))).Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.GetSnapshotAsync(temp.PathFor("backup"), incomplete.Id));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RestoreAsync(temp.PathFor("backup"), incomplete.Id, temp.PathFor("out")));
        Assert.Equal(1, (await engine.CreateSnapshotAsync(new BackupPlan(temp.PathFor("input"), temp.PathFor("backup")))).ReusedFiles);
    }

    [Fact]
    public async Task PhaseOneManifestWithoutExclusionsRemainsReadableAndRestorable()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "legacy");
        var result = await engine.CreateSnapshotAsync(new BackupPlan(temp.PathFor("input"), temp.PathFor("backup")));
        var path = temp.PathFor($"backup/snapshots/{result.Snapshot.Id}/manifest.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json.Remove("Exclusions");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        Assert.Empty((await engine.GetSnapshotAsync(temp.PathFor("backup"), result.Snapshot.Id)).Exclusions);
        Assert.Single(await engine.ListSnapshotsAsync(temp.PathFor("backup")));
        await engine.RestoreAsync(temp.PathFor("backup"), result.Snapshot.Id, temp.PathFor("restore"));
        Assert.Equal("legacy", await File.ReadAllTextAsync(temp.PathFor("restore/source/a")));
    }

    [Fact]
    public async Task MalformedPublishedMetadataIsReportedInsteadOfSilentlyOmitted()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "data");
        var result = await engine.CreateSnapshotAsync(new BackupPlan(temp.PathFor("input"), temp.PathFor("backup")));
        temp.Write($"backup/snapshots/{result.Snapshot.Id}/manifest.json", "{broken");
        await Assert.ThrowsAsync<JsonException>(() => engine.ListSnapshotsAsync(temp.PathFor("backup")));
    }

    [Fact]
    public async Task MissingRepositoriesAreNotCreatedByHistoryReads()
    {
        using var temp = new TestDirectory();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => engine.ListSnapshotsAsync(temp.PathFor("missing")));
        Assert.False(Directory.Exists(temp.PathFor("missing")));
    }

    private static Task Save(TestDirectory temp, SnapshotManifest manifest)
    {
        var path = temp.PathFor($"backup/snapshots/{manifest.Id}/manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest));
    }
}
