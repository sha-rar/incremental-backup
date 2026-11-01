using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace SnapSafe.Core.Tests;

public class BackupEngineTests
{
    private readonly BackupEngine engine = new();
    private static BackupPlan Plan(TestDirectory temp) => new(temp.PathFor("input"), temp.PathFor("backup"));
    private static string StoredPath(TestDirectory temp, SnapshotFile file) =>
        temp.PathFor($"backup/snapshots/{file.StorageSnapshotId}/files/{file.RelativePath}");

    [Fact]
    public async Task InitialBackupRestoresBinaryFilesEmptyFilesAndDirectories()
    {
        using var temp = new TestDirectory();
        temp.CreateDirectory("input/nested/empty");
        var bytes = RandomNumberGenerator.GetBytes(100_003);
        await File.WriteAllBytesAsync(temp.PathFor("input/nested/data.bin"), bytes);
        temp.Write("input/zero", "");
        var result = await engine.CreateSnapshotAsync(Plan(temp));
        Assert.True(result.Snapshot.Complete);
        Assert.Equal(2, result.CopiedFiles);
        Assert.Equal(0, result.ReusedFiles);
        Assert.All(result.Snapshot.Files, file => Assert.Equal(result.Snapshot.Id, file.StorageSnapshotId));
        await engine.RestoreAsync(temp.PathFor("backup"), result.Snapshot.Id, temp.PathFor("restore"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(temp.PathFor("restore/source/nested/data.bin")));
        Assert.Empty(await File.ReadAllBytesAsync(temp.PathFor("restore/source/zero")));
        Assert.True(Directory.Exists(temp.PathFor("restore/source/nested/empty")));
    }

    [Fact]
    public async Task IncrementalSnapshotHashesContentsAndPreservesDeletedVersions()
    {
        using var temp = new TestDirectory();
        var changed = temp.Write("input/change.txt", "old!");
        temp.Write("input/same.txt", "unchanged");
        var deleted = temp.Write("input/deleted.txt", "recover me");
        var timestamp = File.GetLastWriteTimeUtc(changed);
        var first = await engine.CreateSnapshotAsync(Plan(temp));
        await File.WriteAllTextAsync(changed, "new!");
        File.SetLastWriteTimeUtc(changed, timestamp);
        File.Delete(deleted);
        temp.Write("input/added.txt", "added");
        var second = await engine.CreateSnapshotAsync(Plan(temp));
        Assert.Equal(2, second.CopiedFiles);
        Assert.Equal(1, second.ReusedFiles);
        Assert.Equal(first.Snapshot.Id, second.Snapshot.Files.Single(f => f.RelativePath == "source/same.txt").StorageSnapshotId);
        Assert.Equal(2, Directory.GetFiles(temp.PathFor($"backup/snapshots/{second.Snapshot.Id}/files"), "*", SearchOption.AllDirectories).Length);
        Directory.Delete(temp.PathFor("input"), true);
        await engine.RestoreAsync(temp.PathFor("backup"), first.Snapshot.Id, temp.PathFor("old"));
        await engine.RestoreAsync(temp.PathFor("backup"), second.Snapshot.Id, temp.PathFor("new"));
        Assert.Equal("old!", await File.ReadAllTextAsync(temp.PathFor("old/source/change.txt")));
        Assert.Equal("recover me", await File.ReadAllTextAsync(temp.PathFor("old/source/deleted.txt")));
        Assert.Equal("new!", await File.ReadAllTextAsync(temp.PathFor("new/source/change.txt")));
        Assert.False(File.Exists(temp.PathFor("new/source/deleted.txt")));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(temp.PathFor("new/source/same.txt")));
    }

    [Fact]
    public async Task RepeatedUnchangedBackupsReferenceOriginalStorageAndFullModeCopiesAgain()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var first = await engine.CreateSnapshotAsync(Plan(temp));
        for (var i = 0; i < 3; i++)
        {
            var incremental = await engine.CreateSnapshotAsync(Plan(temp));
            Assert.Equal(0, incremental.CopiedFiles);
            Assert.Equal(1, incremental.ReusedFiles);
            Assert.Equal(first.Snapshot.Id, Assert.Single(incremental.Snapshot.Files).StorageSnapshotId);
        }
        var full = await engine.CreateSnapshotAsync(Plan(temp), fullBackup: true);
        Assert.Equal(1, full.CopiedFiles);
        Assert.Equal(full.Snapshot.Id, Assert.Single(full.Snapshot.Files).StorageSnapshotId);
    }

    [Fact]
    public async Task MultipleSourcesWithSameFilenameRestoreSeparately()
    {
        using var temp = new TestDirectory();
        temp.Write("one/file", "one");
        temp.Write("two/file", "two");
        var plan = new BackupPlan([new BackupSource("one", temp.PathFor("one")), new BackupSource("two", temp.PathFor("two"))], temp.PathFor("backup"));
        var result = await engine.CreateSnapshotAsync(plan);
        await engine.RestoreAsync(plan.DestinationDirectory, result.Snapshot.Id, temp.PathFor("restore"));
        Assert.Equal("one", await File.ReadAllTextAsync(temp.PathFor("restore/one/file")));
        Assert.Equal("two", await File.ReadAllTextAsync(temp.PathFor("restore/two/file")));
    }

    [Fact]
    public async Task EmptySourceCanBeBackedUpAndRestored()
    {
        using var temp = new TestDirectory();
        temp.CreateDirectory("input");
        var result = await engine.CreateSnapshotAsync(Plan(temp));
        Assert.Empty(result.Snapshot.Files);
        await engine.RestoreAsync(temp.PathFor("backup"), result.Snapshot.Id, temp.PathFor("restore"));
        Assert.True(Directory.Exists(temp.PathFor("restore/source")));
    }

    [Fact]
    public async Task CancellationAfterCopyNeverPublishesSnapshotAndRetrySucceeds()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "first");
        temp.Write("input/b", "second");
        var first = await engine.CreateSnapshotAsync(Plan(temp));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.CreateSnapshotAsync(Plan(temp), true,
            new InlineProgress(_ => cancellation.Cancel()), cancellation.Token));
        Assert.Single(Directory.GetDirectories(temp.PathFor("backup/snapshots")));
        await engine.RestoreAsync(temp.PathFor("backup"), first.Snapshot.Id, temp.PathFor("restore"));
        var retry = await engine.CreateSnapshotAsync(Plan(temp));
        Assert.Equal(2, retry.ReusedFiles);
    }

    [Fact]
    public async Task RetryRemovesStaleStagingButRetainsCompletedSnapshots()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var first = await engine.CreateSnapshotAsync(Plan(temp));
        var pending = "backup/snapshots/.pending-" + Guid.NewGuid().ToString("N");
        temp.Write(pending + "/files/partial", "unfinished");
        temp.Write(pending + "/manifest.json", "{ broken json");
        var retry = await engine.CreateSnapshotAsync(Plan(temp));
        Assert.False(Directory.Exists(temp.PathFor(pending)));
        Assert.True(Directory.Exists(temp.PathFor($"backup/snapshots/{first.Snapshot.Id}")));
        Assert.Equal(1, retry.ReusedFiles);
    }

    [Fact]
    public async Task LockedSourceDoesNotPublishPartialSnapshot()
    {
        using var temp = new TestDirectory();
        var path = temp.Write("input/a", "locked");
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => engine.CreateSnapshotAsync(Plan(temp)));
        Assert.Empty(Directory.GetDirectories(temp.PathFor("backup/snapshots")));
        Assert.Equal(1, (await engine.CreateSnapshotAsync(Plan(temp))).CopiedFiles);
    }

    [Fact]
    public async Task RepositoryLockRejectsConcurrentBackup()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        temp.CreateDirectory("backup");
        using (File.Open(temp.PathFor("backup/.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => engine.CreateSnapshotAsync(Plan(temp)));
        Assert.Equal(1, (await engine.CreateSnapshotAsync(Plan(temp))).CopiedFiles);
    }

    [Fact]
    public async Task CorruptStoredDataIsRejectedBeforeRestoreAndBeforeReuse()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "good");
        var first = await engine.CreateSnapshotAsync(Plan(temp));
        await File.WriteAllTextAsync(StoredPath(temp, Assert.Single(first.Snapshot.Files)), "evil");
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RestoreAsync(temp.PathFor("backup"), first.Snapshot.Id, temp.PathFor("restore")));
        Assert.False(File.Exists(temp.PathFor("restore/source/file")));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.CreateSnapshotAsync(Plan(temp)));
        Assert.Single(Directory.GetDirectories(temp.PathFor("backup/snapshots")));
        Assert.Equal(1, (await engine.CreateSnapshotAsync(Plan(temp), fullBackup: true)).CopiedFiles);
    }

    [Fact]
    public async Task RestoreRejectsNonemptyAndOverlappingDestinations()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var first = await engine.CreateSnapshotAsync(Plan(temp));
        temp.Write("restore/keep", "user data");
        await Assert.ThrowsAsync<IOException>(() => engine.RestoreAsync(temp.PathFor("backup"), first.Snapshot.Id, temp.PathFor("restore")));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RestoreAsync(temp.PathFor("backup"), first.Snapshot.Id, temp.PathFor("backup/restored")));
        Assert.Equal("user data", await File.ReadAllTextAsync(temp.PathFor("restore/keep")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("source/../../outside")]
    [InlineData("source/file:stream")]
    public async Task InvalidManifestPathsCannotEscapeRestoreRoot(string path)
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var result = await engine.CreateSnapshotAsync(Plan(temp));
        var malicious = result.Snapshot with { Files = [result.Snapshot.Files[0] with { RelativePath = path }] };
        await File.WriteAllTextAsync(temp.PathFor($"backup/snapshots/{result.Snapshot.Id}/manifest.json"), JsonSerializer.Serialize(malicious));
        await Assert.ThrowsAnyAsync<Exception>(() => engine.RestoreAsync(temp.PathFor("backup"), result.Snapshot.Id, temp.PathFor("restore")));
        Assert.False(Directory.Exists(temp.PathFor("restore")));
    }

    [Fact]
    public async Task PendingSnapshotCannotBeRestored()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var result = await engine.CreateSnapshotAsync(Plan(temp));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RestoreAsync(temp.PathFor("backup"), ".pending-" + result.Snapshot.Id, temp.PathFor("restore")));
    }

    [Theory]
    [InlineData("Files")]
    [InlineData("Directories")]
    [InlineData("FormatVersion")]
    [InlineData("CreatedAtUtc")]
    public async Task MissingManifestFieldsAreRejectedInsteadOfRestoringIncompleteData(string field)
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var result = await engine.CreateSnapshotAsync(Plan(temp));
        var path = temp.PathFor($"backup/snapshots/{result.Snapshot.Id}/manifest.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(json.Remove(field));
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await Assert.ThrowsAsync<JsonException>(() => engine.RestoreAsync(temp.PathFor("backup"), result.Snapshot.Id, temp.PathFor("restore")));
        Assert.False(Directory.Exists(temp.PathFor("restore")));
    }

    [Fact]
    public async Task InvalidEmptyDirectoryNameCannotProduceCompletedSnapshot()
    {
        using var temp = new TestDirectory();
        temp.CreateDirectory("input");
        // Extended-length Windows paths can create names regular restore paths cannot safely represent.
        var invalid = (OperatingSystem.IsWindows() ? @"\\?\" : "") + Path.Combine(temp.Root, "input", "bad.");
        Directory.CreateDirectory(invalid);
        var plan = new BackupPlan((OperatingSystem.IsWindows() ? @"\\?\" : "") + temp.PathFor("input"), temp.PathFor("backup"));
        try
        {
            Assert.Contains(Directory.GetDirectories(temp.PathFor("input")), path => Path.GetFileName(path) == "bad.");
            await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateSnapshotAsync(plan));
            Assert.Empty(Directory.GetDirectories(temp.PathFor("backup/snapshots")));
        }
        finally
        {
            Directory.Delete(invalid);
        }
    }

    [Fact]
    public async Task ExtendedWindowsPathAliasCannotBypassRestoreOverlapCheck()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var result = await engine.CreateSnapshotAsync(Plan(temp));
        var repository = (OperatingSystem.IsWindows() ? @"\\?\" : "") + temp.PathFor("backup");
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RestoreAsync(repository, result.Snapshot.Id, temp.PathFor("backup/restored")));
        Assert.False(Directory.Exists(temp.PathFor("backup/restored")));
    }

    private sealed class InlineProgress(Action<BackupProgress> report) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => report(value);
    }
}
