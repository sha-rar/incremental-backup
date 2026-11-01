using SnapSafe.Core;

namespace SnapSafe.Desktop.Model;

public sealed record RepositoryOverview(IReadOnlyList<SnapshotSummary> Snapshots, long StorageBytes);

public interface IDesktopBackupService
{
    Task<BackupPlan> ValidateAsync(BackupPlan plan, CancellationToken token);
    Task<RepositoryOverview> OverviewAsync(string repository, CancellationToken token);
    Task<SnapshotManifest> InspectAsync(string repository, string snapshotId, CancellationToken token);
    Task<BackupResult> BackupAsync(BackupPlan plan, bool full, IProgress<BackupProgress> progress, CancellationToken token);
    Task<RestoreResult> RestoreAsync(string repository, string snapshotId, string destination, RestoreOptions options, CancellationToken token);
}

public sealed class DesktopBackupService : IDesktopBackupService
{
    private readonly BackupEngine engine = new();
    public Task<BackupPlan> ValidateAsync(BackupPlan plan, CancellationToken token) => Task.Run(plan.ValidateForBackup, token);
    public Task<RepositoryOverview> OverviewAsync(string repository, CancellationToken token) => Task.Run(async () =>
    {
        new DestinationProbe().EnsureAccessible(repository);
        if (!Directory.Exists(Path.Combine(repository, "snapshots")))
            return new RepositoryOverview([], 0);
        var snapshots = await engine.ListSnapshotsAsync(repository, token);
        long bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (var path in Directory.EnumerateFiles(repository, "*", options))
        {
            token.ThrowIfCancellationRequested();
            bytes = checked(bytes + new FileInfo(path).Length);
        }
        return new RepositoryOverview(snapshots, bytes);
    }, token);
    public Task<SnapshotManifest> InspectAsync(string repository, string snapshotId, CancellationToken token) =>
        Task.Run(() => engine.GetSnapshotAsync(repository, snapshotId, token), token);
    public Task<BackupResult> BackupAsync(BackupPlan plan, bool full, IProgress<BackupProgress> progress, CancellationToken token) =>
        Task.Run(() => engine.CreateSnapshotAsync(plan, full, progress, token), token);
    public Task<RestoreResult> RestoreAsync(string repository, string snapshotId, string destination, RestoreOptions options, CancellationToken token) =>
        Task.Run(() => engine.RestoreAsync(repository, snapshotId, destination, options, token), token);
}
