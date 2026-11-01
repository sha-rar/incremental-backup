namespace SnapSafe.Core;

public enum RestoreConflictPolicy { Fail, Skip, Overwrite, Rename }

public sealed record RestoreOptions
{
    public IReadOnlyList<string> Paths { get; init; } = [];
    public RestoreConflictPolicy ConflictPolicy { get; init; } = RestoreConflictPolicy.Fail;
}

public sealed record RestoreRename(string OriginalPath, string RestoredPath);
public sealed record RestoreResult(int RestoredFiles, int SkippedFiles, IReadOnlyList<RestoreRename> RenamedFiles);
