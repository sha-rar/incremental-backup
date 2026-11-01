namespace SnapSafe.Core;

/// <summary>A backup job's source and destination (engine to be implemented).</summary>
public sealed record BackupPlan(string SourceDirectory, string DestinationDirectory);
