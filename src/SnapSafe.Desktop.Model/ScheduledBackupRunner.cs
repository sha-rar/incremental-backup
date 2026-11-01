using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapSafe.Desktop.Model;

public sealed class ScheduledBackupRunner(SettingsStore settings, BackupJobRunner runner, TimeProvider? clock = null)
{
    public string StatePath => settings.FilePath + ".schedule.json";
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public async Task<IReadOnlyList<JobLogEntry>> RunDueAsync(CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        FileStream lease;
        try { lease = new FileStream(StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 or 11) { return []; }
        await using (lease)
        {
            var configuration = await settings.LoadAsync(token);
            ScheduleState state = new();
            if (File.Exists(StatePath))
            {
                await using var input = File.OpenRead(StatePath);
                state = await JsonSerializer.DeserializeAsync<ScheduleState>(input, cancellationToken: token) ?? throw new InvalidDataException("Empty scheduling state.");
                if (state.FormatVersion != 1 || state.Jobs is null) throw new InvalidDataException("Unsupported scheduling state.");
            }
            var ids = configuration.Jobs.Select(job => job.Id).ToHashSet();
            foreach (var id in state.Jobs.Keys.Where(id => !ids.Contains(id)).ToArray()) state.Jobs.Remove(id);
            var outcomes = new List<JobLogEntry>();
            foreach (var job in configuration.Jobs.Where(job => job.AutomaticBackupEnabled))
            {
                token.ThrowIfCancellationRequested();
                state.Jobs.TryGetValue(job.Id, out var previous);
                var nextAttempt = previous?.NextAttemptUtc;
                if (previous is not null && previous.IntervalMinutes != job.BackupIntervalMinutes)
                    nextAttempt = previous.LastError is null && previous.LastSuccessUtc.HasValue
                        ? previous.LastSuccessUtc.Value.AddMinutes(job.BackupIntervalMinutes)
                        : (previous.LastAttemptUtc ?? time.GetUtcNow()).AddMinutes(Math.Min(5, job.BackupIntervalMinutes));
                if (nextAttempt > time.GetUtcNow()) continue;
                try
                {
                    var result = await runner.RunAsync(job, token: token);
                    outcomes.Add(result.Log);
                    state.Jobs[job.Id] = new(time.GetUtcNow(), time.GetUtcNow().AddMinutes(job.BackupIntervalMinutes), null, job.BackupIntervalMinutes, time.GetUtcNow());
                }
                catch (Exception exception)
                {
                    outcomes.Add(new(time.GetUtcNow(), job.Id, job.Name, exception is OperationCanceledException ? "Cancelled" : "Failed", exception.Message, 0));
                    state.Jobs[job.Id] = new(previous?.LastSuccessUtc, time.GetUtcNow().AddMinutes(Math.Min(5, job.BackupIntervalMinutes)), exception.Message, job.BackupIntervalMinutes, time.GetUtcNow());
                }
                await AtomicJsonFile.SaveAsync(StatePath, state, CancellationToken.None);
                token.ThrowIfCancellationRequested();
            }
            return outcomes;
        }
    }
    private sealed record ScheduleEntry(DateTimeOffset? LastSuccessUtc, DateTimeOffset NextAttemptUtc, string? LastError, int IntervalMinutes = 0, DateTimeOffset? LastAttemptUtc = null);
    private sealed record ScheduleState
    {
        [JsonRequired] public int FormatVersion { get; init; } = 1;
        [JsonRequired] public Dictionary<Guid, ScheduleEntry> Jobs { get; init; } = [];
    }
}
