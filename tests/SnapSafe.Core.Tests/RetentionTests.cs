using System.Text.Json;
using Xunit;

namespace SnapSafe.Core.Tests;

public class RetentionTests
{
    private readonly BackupEngine engine = new();
    private static BackupPlan Plan(TestDirectory temp) => new(temp.PathFor("input"), temp.PathFor("backup"));

    [Fact]
    public async Task PruningHistoryPreservesSharedBytesAndReclaimsUnreferencedFiles()
    {
        using var temp = new TestDirectory();
        temp.Write("input/shared", "unchanged");
        temp.Write("input/deleted", "old bytes to reclaim");
        var first = await engine.CreateSnapshotAsync(Plan(temp));
        File.Delete(temp.PathFor("input/deleted"));
        var second = await engine.CreateSnapshotAsync(Plan(temp));
        var result = await engine.ApplyRetentionAsync(temp.PathFor("backup"), new(1));
        Assert.Equal(1, result.RemovedSnapshots);
        Assert.Equal(1, result.RemainingSnapshots);
        Assert.True(result.ReclaimedBytes >= 20);
        Assert.Equal(second.Snapshot.Id, Assert.Single(await engine.ListSnapshotsAsync(temp.PathFor("backup"))).Id);
        await Assert.ThrowsAnyAsync<IOException>(() => engine.GetSnapshotAsync(temp.PathFor("backup"), first.Snapshot.Id));
        await engine.RestoreAsync(temp.PathFor("backup"), second.Snapshot.Id, temp.PathFor("restore"));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(temp.PathFor("restore/source/shared")));
        Assert.False(File.Exists(temp.PathFor("restore/source/deleted")));
        var third = await engine.CreateSnapshotAsync(Plan(temp));
        Assert.Equal(1, third.ReusedFiles);
        Assert.Equal(first.Snapshot.Id, Assert.Single(third.Snapshot.Files).StorageSnapshotId);
        Assert.Equal(1, (await engine.ApplyRetentionAsync(temp.PathFor("backup"), new(1))).RemovedSnapshots);
        temp.Write("input/shared", "different");
        var fourth = await engine.CreateSnapshotAsync(Plan(temp));
        Assert.True((await engine.ApplyRetentionAsync(temp.PathFor("backup"), new(1))).ReclaimedBytes > 0);
        await engine.RestoreAsync(temp.PathFor("backup"), fourth.Snapshot.Id, temp.PathFor("new"));
        Assert.Equal("different", await File.ReadAllTextAsync(temp.PathFor("new/source/shared")));
    }

    [Fact]
    public async Task MultipleSurvivingVersionsRemainIndependentlyRestorable()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "one");
        temp.Write("input/b", "shared");
        await engine.CreateSnapshotAsync(Plan(temp));
        temp.Write("input/a", "two");
        var second = await engine.CreateSnapshotAsync(Plan(temp));
        File.Delete(temp.PathFor("input/a"));
        var third = await engine.CreateSnapshotAsync(Plan(temp));
        await engine.ApplyRetentionAsync(temp.PathFor("backup"), new(2));
        await engine.RestoreAsync(temp.PathFor("backup"), second.Snapshot.Id, temp.PathFor("two"));
        await engine.RestoreAsync(temp.PathFor("backup"), third.Snapshot.Id, temp.PathFor("three"));
        Assert.Equal("two", await File.ReadAllTextAsync(temp.PathFor("two/source/a")));
        Assert.Equal("shared", await File.ReadAllTextAsync(temp.PathFor("three/source/b")));
        Assert.False(File.Exists(temp.PathFor("three/source/a")));
        Assert.Equal(0, (await engine.ApplyRetentionAsync(temp.PathFor("backup"), new(2))).RemovedSnapshots);
    }

    [Fact]
    public async Task AgePolicyAlwaysPreservesNewestSnapshot()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        for (var i = 0; i < 3; i++)
        {
            var result = await engine.CreateSnapshotAsync(Plan(temp));
            var aged = result.Snapshot with { CreatedAtUtc = DateTimeOffset.Parse("2020-01-01T00:00:00Z").AddDays(i) };
            await File.WriteAllTextAsync(temp.PathFor($"backup/snapshots/{aged.Id}/manifest.json"), JsonSerializer.Serialize(aged));
        }
        var resultPolicy = await engine.ApplyRetentionAsync(temp.PathFor("backup"), new(MaximumAge: TimeSpan.FromDays(30)), DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        Assert.Equal(2, resultPolicy.RemovedSnapshots);
        Assert.Equal(1, resultPolicy.RemainingSnapshots);
        var latest = Assert.Single(await engine.ListSnapshotsAsync(temp.PathFor("backup")));
        await engine.RestoreAsync(temp.PathFor("backup"), latest.Id, temp.PathFor("restore"));
    }

    [Fact]
    public async Task CorruptSurvivorAbortsBeforeRemovingHistory()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "old");
        await engine.CreateSnapshotAsync(Plan(temp));
        temp.Write("input/file", "new");
        var second = await engine.CreateSnapshotAsync(Plan(temp));
        temp.Write($"backup/snapshots/{second.Snapshot.Id}/files/source/file", "bad");
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.ApplyRetentionAsync(temp.PathFor("backup"), new(1)));
        Assert.Equal(2, (await engine.ListSnapshotsAsync(temp.PathFor("backup"))).Count);
    }

    [Fact]
    public async Task CancellationAndInvalidPolicyDoNotDeleteSnapshots()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        await engine.CreateSnapshotAsync(Plan(temp));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ApplyRetentionAsync(temp.PathFor("backup"), new(1), cancellationToken: cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ApplyRetentionAsync(temp.PathFor("backup"), new(0)));
        Assert.Single(await engine.ListSnapshotsAsync(temp.PathFor("backup")));
    }
}
