using Xunit;

namespace SnapSafe.Core.Tests;

public class SelectiveRestoreTests
{
    private readonly BackupEngine engine = new();
    private Task<BackupResult> Backup(TestDirectory temp) => engine.CreateSnapshotAsync(
        new BackupPlan(temp.PathFor("input"), temp.PathFor("backup")));
    private Task<RestoreResult> Restore(TestDirectory temp, string id, RestoreOptions options) =>
        engine.RestoreAsync(temp.PathFor("backup"), id, temp.PathFor("restore"), options);

    [Fact]
    public async Task SelectedFileRestoresWithoutReadingCorruptUnselectedContents()
    {
        using var temp = new TestDirectory();
        temp.Write("input/nested/a", "selected");
        temp.Write("input/other", "unselected");
        var snapshot = (await Backup(temp)).Snapshot;
        temp.Write($"backup/snapshots/{snapshot.Id}/files/source/other", "corrupt");
        var result = await Restore(temp, snapshot.Id, new() { Paths = ["source/nested/a"] });
        Assert.Equal(1, result.RestoredFiles);
        Assert.Equal("selected", await File.ReadAllTextAsync(temp.PathFor("restore/source/nested/a")));
        Assert.False(File.Exists(temp.PathFor("restore/source/other")));
    }

    [Fact]
    public async Task DirectorySelectionIncludesEmptyDescendantsAndDeduplicatesOverlappingPaths()
    {
        using var temp = new TestDirectory();
        temp.Write("input/nested/a", "selected");
        temp.CreateDirectory("input/nested/empty");
        temp.Write("input/nested-other/file", "not selected");
        var snapshot = (await Backup(temp)).Snapshot;
        var result = await Restore(temp, snapshot.Id, new() { Paths = ["source/nested", "source/nested/a", "source/nested"] });
        Assert.Equal(1, result.RestoredFiles);
        Assert.True(Directory.Exists(temp.PathFor("restore/source/nested/empty")));
        Assert.False(Directory.Exists(temp.PathFor("restore/source/nested-other")));
    }

    [Theory]
    [InlineData("source/missing")]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("source/../../escape")]
    [InlineData("source/a:stream")]
    [InlineData("")]
    public async Task UnknownOrInvalidSelectionFailsBeforeCreatingDestination(string path)
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "data");
        var snapshot = (await Backup(temp)).Snapshot;
        await Assert.ThrowsAsync<ArgumentException>(() => Restore(temp, snapshot.Id, new() { Paths = [path] }));
        Assert.False(Directory.Exists(temp.PathFor("restore")));
    }

    [Fact]
    public async Task DefaultConflictPolicyFailsBeforeRestoringAnyFile()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "first");
        temp.Write("input/z", "last");
        var snapshot = (await Backup(temp)).Snapshot;
        temp.Write("restore/source/z", "keep");
        await Assert.ThrowsAsync<IOException>(() => Restore(temp, snapshot.Id, new()));
        Assert.False(File.Exists(temp.PathFor("restore/source/a")));
        Assert.Equal("keep", await File.ReadAllTextAsync(temp.PathFor("restore/source/z")));
    }

    [Fact]
    public async Task DefaultPolicyAllowsUnrelatedExistingFiles()
    {
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var snapshot = (await Backup(temp)).Snapshot;
        temp.Write("restore/keep", "user data");
        Assert.Equal(1, (await Restore(temp, snapshot.Id, new())).RestoredFiles);
        Assert.Equal("user data", await File.ReadAllTextAsync(temp.PathFor("restore/keep")));
    }

    [Fact]
    public async Task SkipLeavesExistingFileAndDoesNotReadItsDamagedBackup()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "old a");
        temp.Write("input/b", "old b");
        var snapshot = (await Backup(temp)).Snapshot;
        temp.Write("restore/source/a", "user a");
        temp.Write($"backup/snapshots/{snapshot.Id}/files/source/a", "corrupt");
        var result = await Restore(temp, snapshot.Id, new() { ConflictPolicy = RestoreConflictPolicy.Skip });
        Assert.Equal(1, result.RestoredFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal("user a", await File.ReadAllTextAsync(temp.PathFor("restore/source/a")));
        Assert.Equal("old b", await File.ReadAllTextAsync(temp.PathFor("restore/source/b")));
    }

    [Fact]
    public async Task OverwriteReplacesOnlySelectedFilesWithVerifiedContents()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "backed up");
        temp.Write("input/b", "unselected");
        var snapshot = (await Backup(temp)).Snapshot;
        temp.Write("restore/source/a", "user a");
        temp.Write("restore/source/b", "user b");
        var result = await Restore(temp, snapshot.Id, new() { Paths = ["source/a"], ConflictPolicy = RestoreConflictPolicy.Overwrite });
        Assert.Equal(1, result.RestoredFiles);
        Assert.Equal("backed up", await File.ReadAllTextAsync(temp.PathFor("restore/source/a")));
        Assert.Equal("user b", await File.ReadAllTextAsync(temp.PathFor("restore/source/b")));
    }

    [Fact]
    public async Task RenameAvoidsExistingFilesAndOtherSelectedSnapshotPaths()
    {
        using var temp = new TestDirectory();
        temp.Write("input/report.txt", "old report");
        temp.Write("input/report (restored 1).txt", "another snapshot file");
        var snapshot = (await Backup(temp)).Snapshot;
        temp.Write("restore/source/report.txt", "user report");
        temp.Write("restore/source/report (restored 2).txt", "user suffix");
        var result = await Restore(temp, snapshot.Id, new() { ConflictPolicy = RestoreConflictPolicy.Rename });
        Assert.Equal(2, result.RestoredFiles);
        Assert.Equal(new RestoreRename("source/report.txt", "source/report (restored 3).txt"), Assert.Single(result.RenamedFiles));
        Assert.Equal("old report", await File.ReadAllTextAsync(temp.PathFor("restore/source/report (restored 3).txt")));
        Assert.Equal("another snapshot file", await File.ReadAllTextAsync(temp.PathFor("restore/source/report (restored 1).txt")));
        Assert.Equal("user report", await File.ReadAllTextAsync(temp.PathFor("restore/source/report.txt")));
        Assert.Equal("user suffix", await File.ReadAllTextAsync(temp.PathFor("restore/source/report (restored 2).txt")));
    }

    [Theory]
    [InlineData(RestoreConflictPolicy.Fail)]
    [InlineData(RestoreConflictPolicy.Skip)]
    [InlineData(RestoreConflictPolicy.Overwrite)]
    [InlineData(RestoreConflictPolicy.Rename)]
    public async Task FileDirectoryTypeConflictsFailWithoutDeletingUserData(RestoreConflictPolicy policy)
    {
        using var temp = new TestDirectory();
        temp.Write("input/nested/file", "data");
        var snapshot = (await Backup(temp)).Snapshot;
        temp.Write("restore/source/nested", "user file where directory is required");
        await Assert.ThrowsAsync<IOException>(() => Restore(temp, snapshot.Id, new() { ConflictPolicy = policy }));
        Assert.Equal("user file where directory is required", await File.ReadAllTextAsync(temp.PathFor("restore/source/nested")));
    }

    [Fact]
    public async Task CorruptSelectedContentPreventsOverwritesAndAllOtherOutput()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "new a");
        temp.Write("input/z", "new z");
        var snapshot = (await Backup(temp)).Snapshot;
        temp.Write("restore/source/a", "user a");
        temp.Write($"backup/snapshots/{snapshot.Id}/files/source/z", "damaged");
        await Assert.ThrowsAsync<InvalidDataException>(() => Restore(temp, snapshot.Id, new() { ConflictPolicy = RestoreConflictPolicy.Overwrite }));
        Assert.Equal("user a", await File.ReadAllTextAsync(temp.PathFor("restore/source/a")));
        Assert.False(File.Exists(temp.PathFor("restore/source/z")));
        Assert.Empty(Directory.GetFiles(temp.PathFor("restore"), ".snapsafe-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LockedOverwriteTargetIsPreservedAndTemporaryCopyIsRemoved()
    {
        using var temp = new TestDirectory();
        temp.Write("input/a", "new");
        var snapshot = (await Backup(temp)).Snapshot;
        var path = temp.Write("restore/source/a", "keep");
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => Restore(temp, snapshot.Id, new() { ConflictPolicy = RestoreConflictPolicy.Overwrite }));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a filesystem error, got {error?.GetType().Name ?? "success"}.");
        }
        Assert.Equal("keep", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(temp.PathFor("restore"), ".snapsafe-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WindowsDeviceAliasCannotAllowOverwriteRestorationInsideRepository()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var temp = new TestDirectory();
        temp.Write("input/file", "data");
        var snapshot = (await Backup(temp)).Snapshot;
        var alias = @"\\.\" + temp.PathFor("backup");
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RestoreAsync(alias, snapshot.Id, temp.PathFor("backup/restored"),
            new RestoreOptions { ConflictPolicy = RestoreConflictPolicy.Overwrite }));
        Assert.False(Directory.Exists(temp.PathFor("backup/restored")));
    }
}
