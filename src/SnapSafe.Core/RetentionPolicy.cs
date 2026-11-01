namespace SnapSafe.Core;

public sealed record RetentionPolicy(int? MaximumSnapshots = null, TimeSpan? MaximumAge = null)
{
    public bool Enabled => MaximumSnapshots.HasValue || MaximumAge.HasValue;
    public void Validate()
    {
        if (MaximumSnapshots is <= 0 || MaximumAge is { } age && age <= TimeSpan.Zero)
            throw new ArgumentException("Retention requires a positive count or age.");
    }
}

public sealed record RetentionResult(int RemovedSnapshots, int RemainingSnapshots, long ReclaimedBytes);
