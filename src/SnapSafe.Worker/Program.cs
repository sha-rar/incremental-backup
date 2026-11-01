using System.Text.Json;
using SnapSafe.Desktop.Model;
using SnapSafe.Notifications;

namespace SnapSafe.Worker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.WriteLine("SnapSafe.Worker --settings <file.json> --once [--no-notify]\nSnapSafe.Worker --settings <file.json> --register|--unregister\nRuns enabled jobs that are due; persists next-run state and JSONL logs beside settings."); return 0;
        }
        if (args.Length is < 3 or > 4 || args[0] != "--settings" || args[2] is not ("--once" or "--register" or "--unregister") || args.Length == 4 && (args[3] != "--no-notify" || args[2] != "--once")) return 2;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var store = new SettingsStore(args[1]); var logs = new JobLogStore(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "logs"));
        try
        {
            if (args[2] != "--once")
            {
                if (args[2] == "--register" && !File.Exists(store.FilePath)) throw new FileNotFoundException("Settings file does not exist.", store.FilePath);
                if (args[2] == "--register") _ = await store.LoadAsync(cancellation.Token);
                await new TaskSchedulerRegistration(Path.Combine(AppContext.BaseDirectory, "SnapSafe.Worker.exe")).SetEnabledAsync(args[2] == "--register", store.FilePath, cancellation.Token);
                Console.WriteLine(args[2] == "--register" ? "Automatic backup task registered." : "Automatic backup task removed."); return 0;
            }
            var scheduler = new ScheduledBackupRunner(store, new BackupJobRunner(new DesktopBackupService(), logs));
            var outcomes = await scheduler.RunDueAsync(cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(outcomes));
            var settings = await store.LoadAsync(cancellation.Token);
            if (outcomes.Count > 0 && settings.NotificationsEnabled && args.Length == 3)
            {
                var failures = outcomes.Count(entry => entry.Outcome != "Success");
                await WindowsNotifications.ShowAsync("SnapSafe scheduled backups", failures == 0 ? $"{outcomes.Count} backup job(s) completed." : $"{failures} job(s) need attention. Open Activity in SnapSafe for details.", failures > 0);
            }
            return outcomes.Any(entry => entry.Outcome == "Failed") ? 1 : 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception exception)
        {
            try { await logs.AppendAsync(new(DateTimeOffset.UtcNow, Guid.Empty, "Scheduler", "Failed", exception.Message, 0)); } catch (Exception) { }
            Console.Error.WriteLine(exception.Message); return 1;
        }
    }
}
