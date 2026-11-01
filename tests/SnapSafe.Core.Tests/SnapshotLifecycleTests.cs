using System.Text.Json;
using Xunit;

namespace SnapSafe.Core.Tests;

public class SnapshotLifecycleTests
{
    private readonly BackupEngine engine = new();

    [Fact]
    public async Task InterruptedIncrementalSnapshotLeavesHistoryAndSelectiveRecoveryIntact()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "version one");
        temp.Write("input/b", "unchanged");
        var plan = new BackupPlan(temp.PathFor("input"), temp.PathFor("backup"));
        var first = await engine.CreateSnapshotAsync(plan);
        temp.Write("input/a", "version two");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.CreateSnapshotAsync(plan,
            progress: new CancelAfterFile(cancellation), cancellationToken: cancellation.Token));
        Assert.Equal(first.Snapshot.Id, Assert.Single(await engine.ListSnapshotsAsync(plan.DestinationDirectory)).Id);
        var second = await engine.CreateSnapshotAsync(plan);
        Assert.Equal(1, second.CopiedFiles);
        Assert.Equal(1, second.ReusedFiles);
        File.Delete(temp.PathFor("input/a"));
        var third = await engine.CreateSnapshotAsync(plan);
        Assert.Single(third.Snapshot.Files);
        await engine.RestoreAsync(plan.DestinationDirectory, first.Snapshot.Id, temp.PathFor("restore"),
            new RestoreOptions { Paths = ["source/a"] });
        Assert.Equal("version one", await File.ReadAllTextAsync(temp.PathFor("restore/source/a")));
        await engine.RestoreAsync(plan.DestinationDirectory, second.Snapshot.Id, temp.PathFor("restore"),
            new RestoreOptions { Paths = ["source/a"], ConflictPolicy = RestoreConflictPolicy.Overwrite });
        Assert.Equal("version two", await File.ReadAllTextAsync(temp.PathFor("restore/source/a")));
        Assert.Equal(3, (await engine.ListSnapshotsAsync(plan.DestinationDirectory)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedStorageMustBelongToACompletedSnapshot(bool reuse)
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var plan = new BackupPlan(temp.PathFor("input"), temp.PathFor("backup"));
        var first = await engine.CreateSnapshotAsync(plan);
        var second = await engine.CreateSnapshotAsync(plan);
        temp.Write($"backup/snapshots/{first.Snapshot.Id}/manifest.json", JsonSerializer.Serialize(first.Snapshot with { Complete = false }));
        if (reuse)
            await Assert.ThrowsAsync<InvalidDataException>(() => engine.CreateSnapshotAsync(plan));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => engine.RestoreAsync(plan.DestinationDirectory, second.Snapshot.Id,
                temp.PathFor("restore"), new RestoreOptions { Paths = ["source/file"] }));
        Assert.False(Directory.Exists(temp.PathFor("restore")));
    }

    [Fact]
    public async Task ReferencedStorageMustAgreeWithItsOwningManifest()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var plan = new BackupPlan(temp.PathFor("input"), temp.PathFor("backup"));
        var first = await engine.CreateSnapshotAsync(plan);
        var second = await engine.CreateSnapshotAsync(plan);
        temp.Write($"backup/snapshots/{first.Snapshot.Id}/manifest.json", JsonSerializer.Serialize(first.Snapshot with { Files = [] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RestoreAsync(plan.DestinationDirectory, second.Snapshot.Id,
            temp.PathFor("restore"), new RestoreOptions()));
        Assert.False(Directory.Exists(temp.PathFor("restore")));
    }

    private sealed class CancelAfterFile(CancellationTokenSource cancellation) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => cancellation.Cancel();
    }
}
