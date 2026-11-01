using System.Text.Json;

namespace SnapSafe.Core;

public sealed record BackupSource(string Name, string Directory);

/// <summary>Sources have stable names, used as top-level directories during restore.</summary>
public sealed record BackupPlan
{
    public IReadOnlyList<BackupSource> Sources { get; }
    public string SourceDirectory => Sources[0].Directory;
    public string DestinationDirectory { get; }
    public IReadOnlyList<string> Exclusions { get; }

    public BackupPlan(string sourceDirectory, string destinationDirectory, IEnumerable<string>? exclusions = null)
        : this([new BackupSource("source", sourceDirectory)], destinationDirectory, exclusions) { }

    public BackupPlan(IEnumerable<BackupSource> sources, string destinationDirectory, IEnumerable<string>? exclusions = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var items = sources.ToArray();
        if (items.Length == 0)
            throw new ArgumentException("At least one source is required.", nameof(sources));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in items)
        {
            if (source is null)
                throw new ArgumentException("Sources must not be null.", nameof(sources));
            PathSafety.ValidateSegment(source.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Directory);
            if (!names.Add(source.Name))
                throw new ArgumentException("Source names must be unique.", nameof(sources));
        }
        Sources = Array.AsReadOnly(items);
        DestinationDirectory = destinationDirectory;
        Exclusions = Array.AsReadOnly((exclusions ?? []).Select(ExclusionMatcher.Normalize).ToArray());
    }

    public static async Task<BackupPlan> LoadAsync(string configurationPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(configurationPath);
        await using var stream = File.OpenRead(fullPath);
        var config = await JsonSerializer.DeserializeAsync<Configuration>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
            ?? throw new InvalidDataException("Configuration must be a JSON object.");
        var plan = new BackupPlan(config.Sources ?? [], config.DestinationDirectory ?? "", config.Exclusions);
        var basePath = Path.GetDirectoryName(fullPath)!;
        return new BackupPlan(plan.Sources.Select(s => s with { Directory = Path.GetFullPath(s.Directory, basePath) }),
            Path.GetFullPath(plan.DestinationDirectory, basePath), plan.Exclusions);
    }

    internal BackupPlan ValidateForBackup()
    {
        var destination = PathSafety.DirectoryPath(DestinationDirectory);
        PathSafety.EnsureNoLinks(destination);
        var normalized = Sources.Select(s => s with { Directory = PathSafety.DirectoryPath(s.Directory) }).ToArray();
        foreach (var source in normalized)
        {
            if (!System.IO.Directory.Exists(source.Directory))
                throw new DirectoryNotFoundException($"Source directory does not exist: {source.Directory}");
            PathSafety.EnsureNoLinks(source.Directory);
            if (PathSafety.Overlaps(source.Directory, destination))
                throw new ArgumentException("Sources and backup destination must not overlap.");
        }
        return new BackupPlan(normalized, destination, Exclusions);
    }

    private sealed record Configuration(BackupSource[]? Sources, string? DestinationDirectory, string[]? Exclusions);
}
