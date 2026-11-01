using SnapSafe.Core;
using SnapSafe.Desktop.Model;
using Xunit;

namespace SnapSafe.Desktop.Tests;

public class AutomationSettingsTests
{
    [Fact]
    public void EditorPreservesScheduleAndRetentionAndMarksTheirEditsDirty()
    {
        using var temp = new TestFolder();
        var job = new BackupJobSettings { Name = "Job", Sources = [new BackupSource("docs", temp.At("input"))], DestinationDirectory = temp.At("backup"),
            AutomaticBackupEnabled = true, BackupIntervalMinutes = 90, RetainSnapshotCount = 5, RetainSnapshotDays = 30 };
        var editor = new JobEditor(); editor.Load(job);
        Assert.True(editor.AutomaticBackupEnabled); Assert.Equal("90", editor.IntervalMinutesText);
        Assert.Equal("5", editor.RetainCountText); Assert.Equal("30", editor.RetainDaysText);
        editor.IntervalMinutesText = "120"; Assert.True(editor.IsDirty);
        var saved = editor.ToSettings(job.Id);
        Assert.True(saved.AutomaticBackupEnabled); Assert.Equal(120, saved.BackupIntervalMinutes);
        Assert.Equal(5, saved.RetainSnapshotCount); Assert.Equal(30, saved.RetainSnapshotDays);
    }

    [Fact]
    public void TaskActionQuotesPathsAndUsesSettingsScopedLimitedInteractiveScheduling()
    {
        var settings = @"C:\My backups\settings.json";
        var worker = @"C:\Program Files\SnapSafe\SnapSafe.Worker.exe";
        var definition = TaskSchedulerRegistration.CreateTaskDefinition(worker, settings, "S-1-5-21-123");
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var action = definition.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!;
        Assert.Equal(worker, action.Element(ns + "Command")!.Value);
        Assert.Equal($"--settings \"{settings}\" --once", action.Element(ns + "Arguments")!.Value);
        var principal = definition.Root.Element(ns + "Principals")!.Element(ns + "Principal")!;
        Assert.Equal("LeastPrivilege", principal.Element(ns + "RunLevel")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(ns + "LogonType")!.Value);
        var taskSettings = definition.Root.Element(ns + "Settings")!;
        Assert.Equal("false", taskSettings.Element(ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", taskSettings.Element(ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("IgnoreNew", taskSettings.Element(ns + "MultipleInstancesPolicy")!.Value);
        Assert.Equal(TaskSchedulerRegistration.TaskName(settings), TaskSchedulerRegistration.TaskName(settings.ToUpperInvariant()));
        Assert.NotEqual(TaskSchedulerRegistration.TaskName(settings), TaskSchedulerRegistration.TaskName(@"C:\Other\settings.json"));
    }

    [Fact]
    public async Task OlderSettingsDefaultToNoAutomaticBackupsAndUnlimitedHistory()
    {
        using var temp = new TestFolder();
        var path = temp.Write("settings.json", """{"FormatVersion":1,"Jobs":[],"SelectedJobId":null,"RestoreDirectory":"","ConflictPolicy":"Fail"}""");
        var settings = await new SettingsStore(path).LoadAsync();
        Assert.True(settings.NotificationsEnabled);
        Assert.False(new BackupJobSettings().AutomaticBackupEnabled);
        Assert.False(new BackupJobSettings().Retention.Enabled);
    }

    [Fact]
    public async Task RefreshRetriesAFailedRemovalOfTheLastAutomaticTask()
    {
        using var temp = new TestFolder(); temp.Write("input/file", "data");
        var registration = new Registration();
        var workspace = new DesktopWorkspace(new SettingsStore(temp.At("settings.json")), new DesktopBackupService(), registration);
        await workspace.InitializeAsync(); workspace.NewJob(); workspace.Editor.AddSource(temp.At("input"));
        workspace.Editor.DestinationDirectory = temp.At("backup"); workspace.Editor.AutomaticBackupEnabled = true;
        await workspace.SaveJobAsync(); Assert.Empty(workspace.ErrorMessage);
        registration.Fail = true; workspace.Editor.AutomaticBackupEnabled = false;
        await workspace.SaveJobAsync(); Assert.NotEmpty(workspace.ErrorMessage);
        registration.Fail = false; var attempts = registration.Attempts;
        await workspace.RefreshAsync();
        Assert.True(registration.Attempts > attempts); Assert.False(registration.Enabled);
        Assert.Empty(workspace.ErrorMessage);
    }
    private sealed class Registration : IAutomationRegistration
    {
        public bool Fail, Enabled;
        public int Attempts;
        public Task SetEnabledAsync(bool enabled, string settingsPath, CancellationToken token)
        {
            Attempts++; if (Fail) throw new IOException("Task Scheduler unavailable."); Enabled = enabled; return Task.CompletedTask;
        }
    }
}
