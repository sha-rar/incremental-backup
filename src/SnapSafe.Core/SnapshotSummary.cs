namespace SnapSafe.Core;

public sealed record SnapshotSummary(string Id, DateTimeOffset CreatedAtUtc, int FileCount, long TotalBytes, bool Complete);
