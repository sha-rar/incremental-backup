using System.Text.Json;
using SnapSafe.Core;
using SnapSafe.Desktop.Model;
using Xunit;

namespace SnapSafe.Desktop.Tests;

public class AutomationTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static BackupJobSettings Job(TestFolder temp) => new()
    {
        Name = "Scheduled documents", Sources = [new BackupSource("docs", temp.At("input"))],
        DestinationDirectory = temp.At("backup"), AutomaticBackupEnabled = true, BackupIntervalMinutes = 60, RetainSnapshotCount = 1
    };

    [Fact]
    public async Task ScheduleSurvivesRestartHonorsIntervalsAndPrunesWithoutLosingSharedData()
    {
        using var temp = new TestFolder(); temp.Write("input/file", "data");
        var clock = new Clock(); var store = new SettingsStore(temp.At("settings.json")); var job = Job(temp);
        await store.SaveAsync(new DesktopSettings { Jobs = [job] });
        var logs = new JobLogStore(temp.At("logs"));
        ScheduledBackupRunner Scheduler() => new(store, new BackupJobRunner(new DesktopBackupService(), logs, clock), clock);
        Assert.Equal("Success", Assert.Single(await Scheduler().RunDueAsync()).Outcome);
        Assert.Empty(await Scheduler().RunDueAsync());
        clock.Now = clock.Now.AddMinutes(59); Assert.Empty(await Scheduler().RunDueAsync());
        clock.Now = clock.Now.AddMinutes(1); Assert.Single(await Scheduler().RunDueAsync());
        var history = await new BackupEngine().ListSnapshotsAsync(job.DestinationDirectory);
        var latest = Assert.Single(history);
        await new BackupEngine().RestoreAsync(job.DestinationDirectory, latest.Id, temp.At("restore"));
        Assert.Equal("data", await File.ReadAllTextAsync(temp.At("restore/docs/file")));
        Assert.Equal(2, (await logs.ReadRecentAsync()).Count);
        Assert.True(File.Exists(Scheduler().StatePath));
    }

    [Fact]
    public async Task FailedJobsLogAndRetryWithoutSuppressingOtherJobs()
    {
        using var temp = new TestFolder(); temp.Write("input/file", "data");
        var clock = new Clock(); var store = new SettingsStore(temp.At("settings.json")); var good = Job(temp);
        var bad = good with { Id = Guid.NewGuid(), Name = "Missing source", Sources = [new BackupSource("missing", temp.At("absent"))], DestinationDirectory = temp.At("other") };
        await store.SaveAsync(new DesktopSettings { Jobs = [bad, good] });
        var logs = new JobLogStore(temp.At("logs")); var runner = new ScheduledBackupRunner(store, new(new DesktopBackupService(), logs, clock), clock);
        var entries = await runner.RunDueAsync();
        Assert.Equal(["Failed", "Success"], entries.Select(entry => entry.Outcome));
        Assert.Empty(await runner.RunDueAsync());
        clock.Now = clock.Now.AddMinutes(5);
        var retry = Assert.Single(await runner.RunDueAsync()); Assert.Equal(bad.Id, retry.JobId);
        Assert.Equal(3, (await logs.ReadRecentAsync()).Count);
    }

    [Fact]
    public async Task DisabledSchedulesDoNotCreateBackupsAndCorruptStateIsNotOverwritten()
    {
        using var temp = new TestFolder(); temp.Write("input/file", "data");
        var store = new SettingsStore(temp.At("settings.json"));
        await store.SaveAsync(new DesktopSettings { Jobs = [Job(temp) with { AutomaticBackupEnabled = false }] });
        var scheduler = new ScheduledBackupRunner(store, new(new DesktopBackupService(), new(temp.At("logs"))));
        Assert.Empty(await scheduler.RunDueAsync()); Assert.False(Directory.Exists(temp.At("backup")));
        temp.Write("settings.json.schedule.json", "{broken");
        await Assert.ThrowsAsync<JsonException>(() => scheduler.RunDueAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(scheduler.StatePath));
    }

    [Fact]
    public async Task LogWritesAreStructuredAndReadersTolerateAPartialFinalLine()
    {
        using var temp = new TestFolder(); var logs = new JobLogStore(temp.At("logs"));
        var entry = new JobLogEntry(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), Guid.NewGuid(), "Unicode ✓", "Warning", "line one\nline two", 1.5);
        await logs.AppendAsync(entry);
        Assert.Equal(entry, Assert.Single(await logs.ReadRecentAsync()));
        var file = Assert.Single(Directory.GetFiles(logs.DirectoryPath, "*.jsonl"));
        await File.AppendAllTextAsync(file, "{partial");
        Assert.Contains(entry, await logs.ReadRecentAsync());
    }

    [Fact]
    public async Task SharedDestinationsCannotHaveConflictingRetentionPolicies()
    {
        using var temp = new TestFolder(); var job = Job(temp);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(temp.At("settings.json")).SaveAsync(new DesktopSettings
        { Jobs = [job, job with { Id = Guid.NewGuid(), RetainSnapshotCount = 5 }] }));
    }

    [Fact]
    public async Task ExtendedWindowsAliasCannotBypassSharedRetentionPolicyValidation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TestFolder(); var job = Job(temp);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(temp.At("settings.json")).SaveAsync(new DesktopSettings
        { Jobs = [job, job with { Id = Guid.NewGuid(), DestinationDirectory = @"\\?\" + job.DestinationDirectory, RetainSnapshotCount = 5 }] }));
    }

    [Fact]
    public async Task ChangingIntervalRecomputesNextRunFromPreviousSuccess()
    {
        using var temp = new TestFolder(); temp.Write("input/file", "data");
        var clock = new Clock(); var store = new SettingsStore(temp.At("settings.json")); var job = Job(temp) with { BackupIntervalMinutes = 525600 };
        await store.SaveAsync(new DesktopSettings { Jobs = [job] });
        var scheduler = new ScheduledBackupRunner(store, new(new DesktopBackupService(), new(temp.At("logs")), clock), clock);
        Assert.Single(await scheduler.RunDueAsync());
        clock.Now = clock.Now.AddMinutes(1);
        await store.SaveAsync(new DesktopSettings { Jobs = [job with { BackupIntervalMinutes = 1 }] });
        Assert.Single(await scheduler.RunDueAsync());
    }

    [Fact]
    public async Task LogFailureIsAWarningAndDoesNotUndoACompletedBackup()
    {
        using var temp = new TestFolder(); temp.Write("input/file", "data");
        var blockedLogs = temp.Write("logs-file", "not a directory");
        var result = await new BackupJobRunner(new DesktopBackupService(), new JobLogStore(blockedLogs)).RunAsync(Job(temp));
        Assert.True(result.Backup.Snapshot.Complete); Assert.Equal("Warning", result.Log.Outcome);
        Assert.NotEmpty(result.Warnings);
        Assert.Single(await new BackupEngine().ListSnapshotsAsync(temp.At("backup")));
    }

    [Fact]
    public async Task ConcurrentWorkersCannotRunTheSameJobAndCancelledAttemptsAreRecorded()
    {
        using var temp = new TestFolder(); temp.Write("input/file", "data");
        var store = new SettingsStore(temp.At("settings.json")); await store.SaveAsync(new DesktopSettings { Jobs = [Job(temp)] });
        var service = new WaitingService(); var logs = new JobLogStore(temp.At("logs"));
        var scheduler = new ScheduledBackupRunner(store, new(service, logs));
        using var cancellation = new CancellationTokenSource();
        var active = scheduler.RunDueAsync(cancellation.Token);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(await new ScheduledBackupRunner(store, new(new DesktopBackupService(), logs)).RunDueAsync());
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.Equal("Cancelled", Assert.Single(await logs.ReadRecentAsync()).Outcome);
        Assert.False(Directory.Exists(temp.At("backup")));
        Assert.True(File.Exists(scheduler.StatePath));
    }
    private sealed class WaitingService : IDesktopBackupService
    {
        private readonly DesktopBackupService real = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BackupPlan> ValidateAsync(BackupPlan plan, CancellationToken token) => real.ValidateAsync(plan, token);
        public Task<RepositoryOverview> OverviewAsync(string repository, CancellationToken token) => real.OverviewAsync(repository, token);
        public Task<SnapshotManifest> InspectAsync(string repository, string id, CancellationToken token) => real.InspectAsync(repository, id, token);
        public Task<RestoreResult> RestoreAsync(string repository, string id, string destination, RestoreOptions options, CancellationToken token) => real.RestoreAsync(repository, id, destination, options, token);
        public async Task<BackupResult> BackupAsync(BackupPlan plan, bool full, IProgress<BackupProgress> progress, CancellationToken token)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
    }
}
