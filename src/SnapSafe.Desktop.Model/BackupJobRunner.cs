using SnapSafe.Core;
using System.Diagnostics;

namespace SnapSafe.Desktop.Model;

public sealed record JobRunResult(BackupResult Backup, IReadOnlyList<string> Warnings, JobLogEntry Log);
public sealed class BackupJobRunner(IDesktopBackupService service, JobLogStore logs, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public async Task<JobRunResult> RunAsync(BackupJobSettings job, IProgress<BackupProgress>? progress = null, CancellationToken token = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await service.BackupAsync(job.ToPlan(), job.FullBackup, progress ?? new SilentProgress(), token);
            var warnings = new List<string>();
            if (job.Retention.Enabled)
            {
                try { await Task.Run(() => new BackupEngine().ApplyRetentionAsync(job.DestinationDirectory, job.Retention, time.GetUtcNow(), token), token); }
                catch (Exception exception) { warnings.Add($"Backup completed, but retention did not finish: {exception.Message}"); }
            }
            var entry = new JobLogEntry(time.GetUtcNow(), job.Id, job.Name, warnings.Count == 0 ? "Success" : "Warning",
                $"{result.CopiedFiles} copied, {result.ReusedFiles} reused. " + string.Join(" ", warnings), watch.Elapsed.TotalSeconds);
            try { await logs.AppendAsync(entry, CancellationToken.None); }
            catch (Exception exception) { warnings.Add($"Backup completed, but its log could not be written: {exception.Message}"); }
            return new(result, warnings, warnings.Count == 0 ? entry : entry with { Outcome = "Warning", Message = $"{result.CopiedFiles} copied, {result.ReusedFiles} reused. " + string.Join(" ", warnings) });
        }
        catch (Exception exception)
        {
            try { await logs.AppendAsync(new(time.GetUtcNow(), job.Id, job.Name, exception is OperationCanceledException ? "Cancelled" : "Failed", exception.Message, watch.Elapsed.TotalSeconds)); }
            catch (Exception) { /* Preserve the actual backup error when logging is unavailable. */ }
            throw;
        }
    }
    private sealed class SilentProgress : IProgress<BackupProgress> { public void Report(BackupProgress value) { } }
}
