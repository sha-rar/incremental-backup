using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapSafe.Desktop.Model;

public sealed class SettingsStore(string path)
{
    public string FilePath { get; } = Path.GetFullPath(path);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    public async Task<DesktopSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(FilePath))
            return new DesktopSettings();
        await using var input = File.OpenRead(FilePath);
        var settings = await JsonSerializer.DeserializeAsync<DesktopSettings>(input, Options, cancellationToken)
            ?? throw new InvalidDataException("Settings must contain a JSON object.");
        Validate(settings);
        return settings;
    }

    public async Task SaveAsync(DesktopSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(settings);
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await JsonSerializer.SerializeAsync(output, settings, Options, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void Validate(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.FormatVersion != 1 || settings.Jobs is null || !Enum.IsDefined(settings.ConflictPolicy) || settings.RestoreDirectory is null)
            throw new InvalidDataException("Unsupported or invalid desktop settings.");
        var ids = new HashSet<Guid>();
        foreach (var job in settings.Jobs)
        {
            if (job is null || job.Id == Guid.Empty || !ids.Add(job.Id))
                throw new InvalidDataException("Backup jobs must have unique identifiers.");
            ArgumentException.ThrowIfNullOrWhiteSpace(job.Name);
            if (job.Sources is null || job.Exclusions is null)
                throw new InvalidDataException("Invalid backup job settings.");
            _ = job.ToPlan();
            if (job.BackupIntervalMinutes is < 1 or > 525600 || job.RetainSnapshotDays is <= 0 or > 36500 || job.RetainSnapshotCount is <= 0)
                throw new InvalidDataException("Schedule interval and retention limits must be positive and within the supported range.");
            job.Retention.Validate();
            if (!Path.IsPathFullyQualified(job.DestinationDirectory) || job.Sources.Any(source => !Path.IsPathFullyQualified(source.Directory)))
                throw new InvalidDataException("Saved backup directories must use absolute paths.");
        }
        // Retention and incremental history are repository-wide, so each job needs its own repository.
        var repositories = settings.Jobs.Select(job => RepositoryKey(job.DestinationDirectory)).ToArray();
        for (var i = 0; i < repositories.Length; i++)
            for (var j = i + 1; j < repositories.Length; j++)
                if (Overlaps(repositories[i], repositories[j]))
                    throw new InvalidDataException($"Backup jobs \"{settings.Jobs[i].Name}\" and \"{settings.Jobs[j].Name}\" must use separate, non-nested backup destinations.");
        if (settings.SelectedJobId is { } selected && !ids.Contains(selected))
            throw new InvalidDataException("Selected backup job does not exist.");
    }
    private static string RepositoryKey(string directory)
    {
        var path = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows())
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
            else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
            path = Path.GetFullPath(path);
        }
        return Path.TrimEndingDirectorySeparator(path);
    }
    private static bool Overlaps(string first, string second)
    {
        static bool Contains(string parent, string child) => string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
            child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return Contains(first, second) || Contains(second, first);
    }
}
