using Xunit;

namespace SnapSafe.Core.Tests;

public class DestinationAndPerformanceTests
{
    [Fact]
    public async Task InitialAndFullBackupsReadEachSourceOnceAndStillRestoreIdentically()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "content");
        var plan = new BackupPlan(temp.PathFor("input"), temp.PathFor("backup"));
        var engine = new BackupEngine();
        var first = await engine.CreateSnapshotAsync(plan);
        Assert.Equal(7, first.SourceBytesRead);
        var unchanged = await engine.CreateSnapshotAsync(plan);
        Assert.Equal(7, unchanged.SourceBytesRead);
        Assert.Equal(0, unchanged.CopiedFiles);
        var full = await engine.CreateSnapshotAsync(plan, fullBackup: true);
        Assert.Equal(7, full.SourceBytesRead);
        temp.Write("input/file", "a longer content");
        var changed = await engine.CreateSnapshotAsync(plan);
        Assert.Equal(16, changed.SourceBytesRead);
        await engine.RestoreAsync(plan.DestinationDirectory, first.Snapshot.Id, temp.PathFor("old"));
        await engine.RestoreAsync(plan.DestinationDirectory, changed.Snapshot.Id, temp.PathFor("new"));
        Assert.Equal("content", await File.ReadAllTextAsync(temp.PathFor("old/source/file")));
        Assert.Equal("a longer content", await File.ReadAllTextAsync(temp.PathFor("new/source/file")));
    }

    [Fact]
    public async Task UnavailableDestinationDoesNotCreateARepository()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "content");
        var engine = new BackupEngine(new CapacityProbe(unavailable: true));
        await Assert.ThrowsAsync<BackupDestinationException>(() => engine.CreateSnapshotAsync(new BackupPlan(temp.PathFor("input"), temp.PathFor("backup"))));
        Assert.False(Directory.Exists(temp.PathFor("backup")));
    }

    [Fact]
    public async Task InsufficientSpaceLeavesPreviousSnapshotsAndRetryWorks()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "content");
        var plan = new BackupPlan(temp.PathFor("input"), temp.PathFor("backup"));
        var first = await new BackupEngine().CreateSnapshotAsync(plan);
        temp.Write("input/file", new string('x', 100_000));
        await Assert.ThrowsAsync<BackupDestinationException>(() => new BackupEngine(new CapacityProbe()).CreateSnapshotAsync(plan));
        Assert.Single(Directory.GetDirectories(temp.PathFor("backup/snapshots")));
        await new BackupEngine().RestoreAsync(plan.DestinationDirectory, first.Snapshot.Id, temp.PathFor("restored"));
        Assert.Equal("content", await File.ReadAllTextAsync(temp.PathFor("restored/source/file")));
        Assert.Equal(1, (await new BackupEngine().CreateSnapshotAsync(plan)).CopiedFiles);
    }

    private sealed class CapacityProbe(bool unavailable = false) : IDestinationProbe
    {
        public void EnsureAccessible(string destination)
        {
            if (unavailable) throw new BackupDestinationException("Destination disconnected.");
        }
        public void EnsureCapacity(string destination, long requiredBytes)
        {
            if (requiredBytes > 65_536) throw new BackupDestinationException("Insufficient destination space.");
        }
    }
}
