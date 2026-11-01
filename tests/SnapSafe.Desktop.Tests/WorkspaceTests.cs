using SnapSafe.Core;
using SnapSafe.Desktop.Model;
using Xunit;

namespace SnapSafe.Desktop.Tests;

public class WorkspaceTests
{
    private static DesktopWorkspace Workspace(TestFolder temp, IDesktopBackupService? service = null) =>
        new(new SettingsStore(temp.At("settings.json")), service ?? new DesktopBackupService());

    private static async Task Configure(DesktopWorkspace workspace, TestFolder temp)
    {
        await workspace.InitializeAsync();
        workspace.NewJob();
        workspace.Editor.Name = "Documents";
        workspace.Editor.AddSource(temp.At("input"));
        workspace.Editor.DestinationDirectory = temp.At("backup");
        workspace.Editor.ExclusionsText = "*.tmp";
        await workspace.SaveJobAsync();
        Assert.Empty(workspace.ErrorMessage);
    }

    [Fact]
    public void FolderSelectionAssignsUniqueStableNamesAndIgnoresDuplicates()
    {
        using var temp = new TestFolder();
        var editor = new JobEditor();
        editor.AddSource(temp.At("one/docs"));
        editor.AddSource(temp.At("two/docs"));
        editor.AddSource(temp.At("one/docs"));
        Assert.Equal(["docs", "docs-2"], editor.Sources.Select(source => source.Name));
        Assert.True(editor.IsDirty);
        editor.Name = "Backup";
        editor.DestinationDirectory = temp.At("backup");
        editor.Load(editor.ToSettings(Guid.NewGuid()));
        Assert.False(editor.IsDirty);
        editor.Sources[0].Name = "documents";
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task DesktopWorkflowPersistsJobsBrowsesHistoryAndRecoversDeletedFile()
    {
        using var temp = new TestFolder();
        var original = temp.Write("input/file.txt", "original");
        temp.Write("input/draft.tmp", "excluded");
        var workspace = Workspace(temp);
        await Configure(workspace, temp);
        await workspace.BackupAsync();
        Assert.Empty(workspace.ErrorMessage);
        var first = Assert.Single(workspace.Snapshots);
        Assert.Equal(1, workspace.SelectedJob!.SnapshotCount);
        Assert.True(workspace.SelectedJob.StorageBytes > 0);
        temp.Write("input/file.txt", "modified");
        await workspace.BackupAsync();
        File.Delete(original);
        await workspace.BackupAsync();
        Assert.Equal(3, workspace.Snapshots.Count);
        await workspace.SelectSnapshotAsync(first);
        workspace.Entries.Single(entry => entry.RelativePath == "input/file.txt").IsSelected = true;
        workspace.RestoreDirectory = temp.At("restore");
        workspace.ConflictPolicy = RestoreConflictPolicy.Rename;
        await workspace.RestoreAsync(all: false);
        Assert.Empty(workspace.ErrorMessage);
        Assert.Equal("original", await File.ReadAllTextAsync(temp.At("restore/input/file.txt")));
        Assert.False(File.Exists(temp.At("restore/input/draft.tmp")));
        var restarted = Workspace(temp);
        await restarted.InitializeAsync();
        Assert.Equal("Documents", Assert.Single(restarted.Jobs).Name);
        Assert.Equal(workspace.SelectedJob.Settings.Id, restarted.SelectedJob!.Settings.Id);
        Assert.Equal(temp.At("restore"), restarted.RestoreDirectory);
        Assert.Equal(RestoreConflictPolicy.Rename, restarted.ConflictPolicy);
        await restarted.RemoveJobAsync();
        Assert.Empty(restarted.Jobs);
        Assert.Equal(3, (await new BackupEngine().ListSnapshotsAsync(temp.At("backup"))).Count);
    }

    [Fact]
    public async Task SelectionResetsWhenSnapshotChanges()
    {
        using var temp = new TestFolder();
        temp.Write("input/file", "old");
        var workspace = Workspace(temp);
        await Configure(workspace, temp);
        await workspace.BackupAsync();
        var first = Assert.Single(workspace.Snapshots);
        temp.Write("input/file", "new");
        await workspace.BackupAsync();
        workspace.Entries[0].IsSelected = true;
        await workspace.SelectSnapshotAsync(first);
        Assert.All(workspace.Entries, entry => Assert.False(entry.IsSelected));
    }

    [Fact]
    public async Task InvalidJobCannotBeSavedAndCorruptSettingsCannotBeOverwritten()
    {
        using var temp = new TestFolder();
        temp.Write("input/file", "data");
        var workspace = Workspace(temp);
        await workspace.InitializeAsync();
        workspace.NewJob();
        workspace.Editor.AddSource(temp.At("input"));
        workspace.Editor.DestinationDirectory = temp.At("input/backup");
        await workspace.SaveJobAsync();
        Assert.NotEmpty(workspace.ErrorMessage);
        Assert.Empty(workspace.Jobs);
        Assert.False(File.Exists(temp.At("settings.json")));
        temp.Write("settings.json", "{broken");
        var broken = Workspace(temp);
        await broken.InitializeAsync();
        Assert.NotEmpty(broken.ErrorMessage);
        Assert.False(broken.CanEdit);
        await broken.SavePreferencesAsync();
        Assert.Equal("{broken", await File.ReadAllTextAsync(temp.At("settings.json")));
    }

    [Fact]
    public async Task BusyBackupGatesEditsAndCancellationRestoresIdleState()
    {
        using var temp = new TestFolder();
        temp.Write("input/file", "data");
        var blocker = new BlockingBackup();
        var workspace = Workspace(temp, blocker);
        await Configure(workspace, temp);
        var pending = workspace.BackupAsync();
        await blocker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(workspace.IsBusy);
        Assert.False(workspace.CanEdit);
        Assert.False(workspace.CanChooseJob);
        var selected = workspace.SelectedJob;
        workspace.NewJob();
        Assert.Same(selected, workspace.SelectedJob);
        workspace.Cancel();
        await pending;
        Assert.False(workspace.IsBusy);
        Assert.Empty(workspace.ErrorMessage);
        Assert.Contains("cancel", workspace.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(workspace.Snapshots);
    }

    private sealed class BlockingBackup : IDesktopBackupService
    {
        private readonly DesktopBackupService real = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BackupPlan> ValidateAsync(BackupPlan plan, CancellationToken token) => real.ValidateAsync(plan, token);
        public Task<RepositoryOverview> OverviewAsync(string repository, CancellationToken token) => real.OverviewAsync(repository, token);
        public Task<SnapshotManifest> InspectAsync(string repository, string id, CancellationToken token) => real.InspectAsync(repository, id, token);
        public Task<RestoreResult> RestoreAsync(string repository, string id, string destination, RestoreOptions options, CancellationToken token) => real.RestoreAsync(repository, id, destination, options, token);
        public async Task<BackupResult> BackupAsync(BackupPlan plan, bool full, IProgress<BackupProgress> progress, CancellationToken token)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Cancellation should end this operation.");
        }
    }

    [Fact]
    public async Task FailedJobSelectionNotifiesTheViewToRestoreTheActiveJob()
    {
        using var temp = new TestFolder();
        temp.Write("input/file", "data");
        var first = new BackupJobSettings { Name = "First", Sources = [new BackupSource("docs", temp.At("input"))], DestinationDirectory = temp.At("backup-one") };
        var second = first with { Id = Guid.NewGuid(), Name = "Second", DestinationDirectory = temp.At("backup-two") };
        var store = new SettingsStore(temp.At("settings.json"));
        await store.SaveAsync(new DesktopSettings { Jobs = [first, second], SelectedJobId = first.Id });
        var workspace = Workspace(temp);
        await workspace.InitializeAsync();
        var notified = false;
        workspace.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(workspace.SelectedJob)) notified = true; };
        using (File.Open(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await workspace.SelectJobAsync(workspace.Jobs[1]);
        Assert.NotEmpty(workspace.ErrorMessage);
        Assert.Equal(first.Id, workspace.SelectedJob!.Settings.Id);
        Assert.Equal("First", workspace.Editor.Name);
        Assert.True(notified);
        Assert.False(workspace.IsBusy);
    }

    [Fact]
    public async Task ClosingPreferencesCannotSilentlyDiscardUnsavedJobEdits()
    {
        using var temp = new TestFolder();
        temp.Write("input/file", "data");
        var workspace = Workspace(temp);
        await Configure(workspace, temp);
        var original = await File.ReadAllTextAsync(temp.At("settings.json"));
        workspace.Editor.ExclusionsText = "private/**";
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.SavePreferencesAsync());
        Assert.Equal(original, await File.ReadAllTextAsync(temp.At("settings.json")));
        Assert.True(workspace.Editor.IsDirty);
    }

    [Fact]
    public async Task MissingPreviouslyUsedRepositoryIsReportedInsteadOfAppearingEmpty()
    {
        using var temp = new TestFolder();
        temp.Write("input/file", "data");
        var workspace = Workspace(temp);
        await Configure(workspace, temp);
        await workspace.BackupAsync();
        Directory.Move(temp.At("backup"), temp.At("offline-backup"));
        await workspace.RefreshAsync();
        Assert.Equal("Unavailable", workspace.SelectedJob!.Status);
        Assert.NotEmpty(workspace.ErrorMessage);
        Assert.Empty(workspace.Snapshots);
        Assert.False(workspace.CanRestore);
        Directory.Move(temp.At("offline-backup"), temp.At("backup"));
        await workspace.RefreshAsync();
        Assert.Empty(workspace.ErrorMessage);
        Assert.Single(workspace.Snapshots);
        Assert.Equal("Ready", workspace.SelectedJob.Status);
    }
}
