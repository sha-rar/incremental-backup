namespace SnapSafe.Core;

public sealed record SnapshotFile(string RelativePath, long Length, string Sha256, string StorageSnapshotId);

public sealed record SnapshotManifest
{
    public int FormatVersion { get; init; } = 1;
    public required string Id { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public bool Complete { get; init; }
    public BackupSource[] Sources { get; init; } = [];
    public string[] Directories { get; init; } = [];
    public SnapshotFile[] Files { get; init; } = [];
}

public sealed record BackupProgress(string RelativePath, int ProcessedFiles, int CopiedFiles);
public sealed record BackupResult(SnapshotManifest Snapshot, int CopiedFiles, int ReusedFiles);
