using System.Collections.ObjectModel;
using SnapSafe.Core;

namespace SnapSafe.Desktop.Model;

public sealed class JobCard(BackupJobSettings settings) : ObservableObject
{
    public BackupJobSettings Settings { get; } = settings;
    public string Name => Settings.Name;
    private string status = "No backups yet";
    private RepositoryOverview overview = new([], 0);
    public string Destination => Settings.DestinationDirectory;
    public int SnapshotCount => overview.Snapshots.Count;
    public long StorageBytes => overview.StorageBytes;
    public string StorageText => DisplaySize.Format(StorageBytes);
    public string LastBackup => overview.Snapshots.FirstOrDefault()?.CreatedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm") ?? "Never";
    public string Status { get => status; set => Set(ref status, value); }
    internal IReadOnlyList<SnapshotSummary> History => overview.Snapshots;
    internal bool Available { get; set; } = true;
    internal void Update(RepositoryOverview value)
    {
        Available = true;
        overview = value;
        Changed(nameof(SnapshotCount)); Changed(nameof(StorageBytes)); Changed(nameof(StorageText)); Changed(nameof(LastBackup));
        Status = SnapshotCount == 0 ? "No backups yet" : "Ready";
    }
}

public sealed class RestoreEntry(string relativePath, bool directory, long length) : ObservableObject
{
    public string RelativePath { get; } = relativePath;
    public bool IsDirectory { get; } = directory;
    public long Length { get; } = length;
    private bool selected;
    public bool IsSelected { get => selected; set => Set(ref selected, value); }
    public string Kind => IsDirectory ? "Folder" : "File";
    public string Size => IsDirectory ? "—" : DisplaySize.Format(Length);
}

public sealed class DesktopWorkspace : ObservableObject
{
    private readonly SettingsStore store;
    private readonly IDesktopBackupService service;
    private readonly IAutomationRegistration? registration;
    private readonly JobLogStore logs;
    private readonly BackupJobRunner jobRunner;
    private bool notifications = true;
    private string automationStatus = "Automatic backups are off.";
    private bool pendingTaskRemoval;
    public bool NotificationsEnabled { get => notifications; set => Set(ref notifications, value); }
    public string AutomationStatus { get => automationStatus; private set => Set(ref automationStatus, value); }
    public ObservableCollection<JobLogEntry> Activity { get; } = [];
    public event Action<JobLogEntry>? BackupFinished;
    private CancellationTokenSource? operation;
    private TaskCompletionSource? completion;
    private bool busy, settingsFailed, initialized;
    private string error = "", status = "Welcome to SnapSafe", progress = "", restoreDirectory = "";
    private RestoreConflictPolicy policy;
    public ObservableCollection<JobCard> Jobs { get; } = [];
    public ObservableCollection<SnapshotSummary> Snapshots { get; } = [];
    public ObservableCollection<RestoreEntry> Entries { get; } = [];
    public JobEditor Editor { get; } = new();
    public JobCard? SelectedJob { get; private set; }
    public SnapshotSummary? SelectedSnapshot { get; private set; }
    public string RestoreDirectory { get => restoreDirectory; set => Set(ref restoreDirectory, value); }
    public RestoreConflictPolicy ConflictPolicy { get => policy; set => Set(ref policy, value); }
    public IReadOnlyList<RestoreConflictPolicy> ConflictPolicies { get; } = Enum.GetValues<RestoreConflictPolicy>();
    public bool IsBusy { get => busy; private set { Set(ref busy, value); StateChanged(); } }
    public bool CanEdit => !IsBusy && !settingsFailed && initialized;
    public bool CanChooseJob => CanEdit && !Editor.IsDirty;
    public bool CanSave => CanEdit && Editor.IsDirty;
    public bool CanRun => CanChooseJob && SelectedJob is not null;
    public bool CanRestore => CanRun && SelectedSnapshot is not null && Entries.Count > 0;
    public bool CanRestoreSelected => CanRestore && Entries.Any(entry => entry.IsSelected);
    public int SelectedEntryCount => Entries.Count(entry => entry.IsSelected);
    public string ErrorMessage { get => error; private set { Set(ref error, value); Changed(nameof(HasError)); } }
    public bool HasError => ErrorMessage.Length > 0;
    public string StatusMessage { get => status; private set => Set(ref status, value); }
    public string ProgressText { get => progress; private set => Set(ref progress, value); }
    public string JobCountText => $"{Jobs.Count} configured {(Jobs.Count == 1 ? "job" : "jobs")}";

    public DesktopWorkspace(SettingsStore store, IDesktopBackupService service, IAutomationRegistration? registration = null)
    {
        this.store = store; this.service = service;
        this.registration = registration;
        logs = new JobLogStore(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "logs"));
        jobRunner = new BackupJobRunner(service, logs);
        Editor.PropertyChanged += (_, _) => StateChanged();
    }
    private void StateChanged()
    {
        foreach (var property in new[] { nameof(CanEdit), nameof(CanChooseJob), nameof(CanSave), nameof(CanRun), nameof(CanRestore), nameof(CanRestoreSelected), nameof(SelectedEntryCount), nameof(JobCountText) }) Changed(property);
    }
    private DesktopSettings Settings(IEnumerable<BackupJobSettings>? jobs = null, Guid? selected = null) => new()
    {
        Jobs = (jobs ?? Jobs.Select(job => job.Settings)).ToArray(), SelectedJobId = selected ?? SelectedJob?.Settings.Id,
        RestoreDirectory = RestoreDirectory, ConflictPolicy = ConflictPolicy, NotificationsEnabled = NotificationsEnabled
    };
    private void SelectDirect(JobCard? job)
    {
        SelectedJob = job; Changed(nameof(SelectedJob)); Editor.Load(job?.Settings); ClearHistory(); StateChanged();
    }
    private void ClearHistory()
    {
        Snapshots.Clear(); ClearEntries(); SelectedSnapshot = null; Changed(nameof(SelectedSnapshot));
    }
    private void ClearEntries()
    {
        foreach (var entry in Entries) entry.PropertyChanged -= EntryChanged;
        Entries.Clear(); StateChanged();
    }
    private void EntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => StateChanged();
    public Task InitializeAsync() => RunAsync("Loading settings…", async token =>
    {
        DesktopSettings settings;
        try { settings = await store.LoadAsync(token); }
        catch { settingsFailed = true; throw; }
        Jobs.Clear();
        foreach (var job in settings.Jobs) Jobs.Add(new JobCard(job));
        RestoreDirectory = settings.RestoreDirectory; ConflictPolicy = settings.ConflictPolicy;
        NotificationsEnabled = settings.NotificationsEnabled;
        SelectDirect(Jobs.FirstOrDefault(job => job.Settings.Id == settings.SelectedJobId) ?? Jobs.FirstOrDefault());
        initialized = true;
        await RefreshCardsAsync(token);
        await LoadHistoryAsync(token);
        await ConfigureAutomationAsync(false, token); await ReadActivityAsync(token);
        StatusMessage = Jobs.Count == 0 ? "Add your first backup job to get started." : "Ready";
    });
    public void NewJob()
    {
        if (!CanChooseJob) return;
        SelectDirect(null); Editor.Name = "Untitled backup";
        StatusMessage = "Choose source folders and a destination, then save your job.";
    }
    public void DiscardEdits()
    {
        if (!CanEdit) return;
        SelectDirect(SelectedJob ?? Jobs.FirstOrDefault());
    }
    public Task SaveJobAsync()
    {
        if (!CanSave) return Task.CompletedTask;
        return RunAsync("Saving job…", async token =>
        {
            var candidate = Editor.ToSettings(SelectedJob?.Settings.Id ?? Guid.NewGuid());
            var previouslyEnabled = Jobs.Any(job => job.Settings.AutomaticBackupEnabled);
            var plan = await service.ValidateAsync(candidate.ToPlan(), token);
            candidate = candidate with { Sources = plan.Sources.ToArray(), DestinationDirectory = plan.DestinationDirectory };
            var jobs = Jobs.Select(job => job.Settings).Where(job => job.Id != candidate.Id).Append(candidate).ToArray();
            await store.SaveAsync(Settings(jobs, candidate.Id), token);
            var card = new JobCard(candidate);
            var existing = Jobs.FirstOrDefault(job => job.Settings.Id == candidate.Id);
            if (existing is null) Jobs.Add(card); else Jobs[Jobs.IndexOf(existing)] = card;
            SelectDirect(card);
            await ConfigureAutomationAsync(previouslyEnabled, token);
            await RefreshCardAsync(card, token); await LoadHistoryAsync(token);
            StatusMessage = "Backup job saved.";
        });
    }
    public Task RemoveJobAsync()
    {
        if (!CanRun) return Task.CompletedTask;
        var removed = SelectedJob!;
        return RunAsync("Removing job…", async token =>
        {
            var remaining = Jobs.Where(job => job != removed).ToArray();
            await store.SaveAsync(Settings(remaining.Select(job => job.Settings), remaining.FirstOrDefault()?.Settings.Id) with
                { SelectedJobId = remaining.FirstOrDefault()?.Settings.Id }, token);
            Jobs.Remove(removed); SelectDirect(remaining.FirstOrDefault());
            await ConfigureAutomationAsync(removed.Settings.AutomaticBackupEnabled, token);
            await LoadHistoryAsync(token); StatusMessage = "Job removed. Its backup snapshots were kept.";
        });
    }
    public async Task SelectJobAsync(JobCard? job)
    {
        if (!CanChooseJob || job is null || job == SelectedJob || !Jobs.Contains(job))
        {
            if (job != SelectedJob) Changed(nameof(SelectedJob));
            return;
        }
        try
        {
            await RunAsync("Loading job…", async token =>
            {
                await store.SaveAsync(Settings(selected: job.Settings.Id), token);
                SelectDirect(job); await RefreshCardAsync(job, token); await LoadHistoryAsync(token); StatusMessage = "Ready";
            });
        }
        finally { Changed(nameof(SelectedJob)); }
    }
    public Task RefreshAsync()
    {
        if (!CanChooseJob) return Task.CompletedTask;
        return RunAsync("Refreshing repositories…", async token => { await RefreshCardsAsync(token); await LoadHistoryAsync(token); await ConfigureAutomationAsync(false, token); await ReadActivityAsync(token); StatusMessage = "Repositories refreshed."; });
    }
    private async Task RefreshCardsAsync(CancellationToken token)
    {
        foreach (var job in Jobs) await RefreshCardAsync(job, token);
    }
    private async Task RefreshCardAsync(JobCard job, CancellationToken token)
    {
        try
        {
            var overview = await service.OverviewAsync(job.Destination, token);
            if (job.SnapshotCount > 0 && overview.Snapshots.Count == 0)
                throw new IOException($"Previously backed-up snapshots are unavailable at {job.Destination}. Check the destination and refresh.");
            job.Update(overview);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { job.Available = false; job.Status = "Unavailable"; ErrorMessage = exception.Message; }
    }
    private async Task LoadHistoryAsync(CancellationToken token)
    {
        ClearHistory();
        if (SelectedJob is null || !SelectedJob.Available) return;
        foreach (var snapshot in SelectedJob.History) Snapshots.Add(snapshot);
        if (Snapshots.FirstOrDefault() is { } first) await InspectAsync(first, token);
    }
    private async Task InspectAsync(SnapshotSummary snapshot, CancellationToken token)
    {
        ClearEntries(); SelectedSnapshot = null; Changed(nameof(SelectedSnapshot));
        var manifest = await service.InspectAsync(SelectedJob!.Destination, snapshot.Id, token);
        foreach (var directory in manifest.Directories) AddEntry(new RestoreEntry(directory, true, 0));
        foreach (var file in manifest.Files) AddEntry(new RestoreEntry(file.RelativePath, false, file.Length));
        SelectedSnapshot = snapshot; Changed(nameof(SelectedSnapshot)); StateChanged();
    }
    private void AddEntry(RestoreEntry entry) { entry.PropertyChanged += EntryChanged; Entries.Add(entry); }
    public Task SelectSnapshotAsync(SnapshotSummary snapshot)
    {
        if (!CanRun || !Snapshots.Contains(snapshot) || snapshot == SelectedSnapshot) return Task.CompletedTask;
        return RunAsync("Reading snapshot…", async token => { await InspectAsync(snapshot, token); StatusMessage = "Snapshot contents loaded."; });
    }
    public Task BackupAsync()
    {
        if (!CanRun) return Task.CompletedTask;
        var job = SelectedJob!;
        return RunAsync("Scanning and backing up…", async token =>
        {
            job.Status = "Backing up";
            var progress = new Progress<BackupProgress>(value =>
            {
                if (IsBusy && operation?.Token == token)
                    ProgressText = $"{value.ProcessedFiles} files processed · {value.CopiedFiles} copied · {value.RelativePath}";
            });
            try
            {
                var run = await jobRunner.RunAsync(job.Settings, progress, token);
                var result = run.Backup;
                await RefreshCardAsync(job, token); await LoadHistoryAsync(token);
                StatusMessage = $"Backup complete: {result.CopiedFiles} copied, {result.ReusedFiles} reused.";
                if (run.Warnings.Count > 0) ErrorMessage = string.Join(Environment.NewLine, run.Warnings);
                BackupFinished?.Invoke(run.Log);
            }
            catch (Exception exception)
            {
                job.Status = token.IsCancellationRequested ? "Cancelled" : "Backup failed";
                if (!token.IsCancellationRequested) BackupFinished?.Invoke(new(DateTimeOffset.UtcNow, job.Settings.Id, job.Name, "Failed", exception.Message, 0));
                throw;
            }
            finally { await ReadActivityAsync(CancellationToken.None); }
        });
    }
    public Task RestoreAsync(bool all)
    {
        if (!(all ? CanRestore : CanRestoreSelected)) return Task.CompletedTask;
        var repository = SelectedJob!.Destination;
        var snapshot = SelectedSnapshot!.Id;
        var destination = RestoreDirectory;
        var options = new RestoreOptions { Paths = all ? [] : Entries.Where(entry => entry.IsSelected).Select(entry => entry.RelativePath).ToArray(), ConflictPolicy = ConflictPolicy };
        return RunAsync("Verifying and restoring files…", async token =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
            await store.SaveAsync(Settings(), token);
            var result = await service.RestoreAsync(repository, snapshot, destination, options, token);
            StatusMessage = $"Restore complete: {result.RestoredFiles} restored, {result.SkippedFiles} skipped, {result.RenamedFiles.Count} renamed.";
        });
    }
    public Task SavePreferencesAsync()
    {
        if (Editor.IsDirty) throw new InvalidOperationException("Save or discard job edits before closing.");
        return initialized && !settingsFailed ? store.SaveAsync(Settings()) : Task.CompletedTask;
    }
    public void Cancel() => operation?.Cancel();
    public Task RefreshActivityAsync() => CanChooseJob ? RunAsync("Loading activity…", async token => { await ReadActivityAsync(token); StatusMessage = "Activity loaded."; }) : Task.CompletedTask;
    private async Task ReadActivityAsync(CancellationToken token)
    {
        try
        {
            var entries = await Task.Run(() => logs.ReadRecentAsync(token), token);
            Activity.Clear(); foreach (var entry in entries) Activity.Add(entry);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { ErrorMessage = $"Activity log unavailable: {exception.Message}"; }
    }
    private async Task ConfigureAutomationAsync(bool previouslyEnabled, CancellationToken token)
    {
        var enabled = Jobs.Any(job => job.Settings.AutomaticBackupEnabled);
        if (registration is null) return;
        if (!enabled && !previouslyEnabled && !pendingTaskRemoval) { AutomationStatus = "Automatic backups are off."; return; }
        try
        {
            await registration.SetEnabledAsync(enabled, store.FilePath, token);
            pendingTaskRemoval = false;
            AutomationStatus = enabled ? "Automatic backups run while you are logged in, even with SnapSafe closed." : "Automatic backups are off.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { pendingTaskRemoval = !enabled; AutomationStatus = "Automatic execution could not be configured. Refresh to retry."; ErrorMessage = $"Job settings saved, but task setup failed: {exception.Message}"; }
    }
    public Task WaitForIdleAsync() => completion?.Task ?? Task.CompletedTask;
    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation; completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsBusy = true; ErrorMessage = ""; StatusMessage = message; ProgressText = "";
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { StatusMessage = "Operation cancelled. Completed snapshots are preserved; finished restored files may remain."; }
        catch (Exception exception) { ErrorMessage = exception.Message; StatusMessage = "Operation failed. See the message above."; }
        finally { operation = null; IsBusy = false; completion.TrySetResult(); }
    }
}
