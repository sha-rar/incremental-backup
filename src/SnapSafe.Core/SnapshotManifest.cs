using System.Text.Json.Serialization;

namespace SnapSafe.Core;

public sealed record SnapshotFile(
    [property: JsonRequired] string RelativePath,
    [property: JsonRequired] long Length,
    [property: JsonRequired] string Sha256,
    [property: JsonRequired] string StorageSnapshotId);

public sealed record SnapshotManifest
{
    [JsonRequired]
    public int FormatVersion { get; init; } = 1;
    public required string Id { get; init; }
    [JsonRequired]
    public DateTimeOffset CreatedAtUtc { get; init; }
    [JsonRequired]
    public bool Complete { get; init; }
    [JsonRequired]
    public BackupSource[] Sources { get; init; } = [];
    [JsonRequired]
    public string[] Directories { get; init; } = [];
    [JsonRequired]
    public SnapshotFile[] Files { get; init; } = [];
}

public sealed record BackupProgress(string RelativePath, int ProcessedFiles, int CopiedFiles);
public sealed record BackupResult(SnapshotManifest Snapshot, int CopiedFiles, int ReusedFiles);
