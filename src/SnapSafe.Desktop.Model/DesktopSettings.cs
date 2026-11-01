using System.Text.Json.Serialization;
using SnapSafe.Core;

namespace SnapSafe.Desktop.Model;

public sealed record BackupJobSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "New backup";
    public BackupSource[] Sources { get; init; } = [];
    public string DestinationDirectory { get; init; } = "";
    public string[] Exclusions { get; init; } = [];
    public bool FullBackup { get; init; }
    public bool AutomaticBackupEnabled { get; init; }
    public int BackupIntervalMinutes { get; init; } = 360;
    public int? RetainSnapshotCount { get; init; }
    public int? RetainSnapshotDays { get; init; }
    [JsonIgnore] public RetentionPolicy Retention => new(RetainSnapshotCount, RetainSnapshotDays is { } days ? TimeSpan.FromDays(days) : null);

    public BackupPlan ToPlan() => new(Sources, DestinationDirectory, Exclusions);
}

public sealed record DesktopSettings
{
    [JsonRequired]
    public int FormatVersion { get; init; } = 1;
    [JsonRequired]
    public BackupJobSettings[] Jobs { get; init; } = [];
    public Guid? SelectedJobId { get; init; }
    public string RestoreDirectory { get; init; } = "";
    public RestoreConflictPolicy ConflictPolicy { get; init; } = RestoreConflictPolicy.Fail;
    public bool NotificationsEnabled { get; init; } = true;
}
