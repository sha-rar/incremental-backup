using System.Collections.ObjectModel;
using SnapSafe.Core;

namespace SnapSafe.Desktop.Model;

public sealed class SourceEditor : ObservableObject
{
    private string name;
    public string Name { get => name; set => Set(ref name, value); }
    public string Directory { get; }
    public SourceEditor(string name, string directory) { this.name = name; Directory = directory; }
}

public sealed class JobEditor : ObservableObject
{
    private string name = "New backup", destination = "", exclusions = "";
    private bool fullBackup, dirty;
    public ObservableCollection<SourceEditor> Sources { get; } = [];
    public string Name { get => name; set { if (Set(ref name, value)) MarkDirty(); } }
    public string DestinationDirectory { get => destination; set { if (Set(ref destination, value)) MarkDirty(); } }
    public string ExclusionsText { get => exclusions; set { if (Set(ref exclusions, value)) MarkDirty(); } }
    public bool FullBackup { get => fullBackup; set { if (Set(ref fullBackup, value)) MarkDirty(); } }
    public bool IsDirty { get => dirty; private set => Set(ref dirty, value); }

    public JobEditor()
    {
        Sources.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
                foreach (SourceEditor source in args.NewItems) source.PropertyChanged += SourceChanged;
            if (args.OldItems is not null)
                foreach (SourceEditor source in args.OldItems) source.PropertyChanged -= SourceChanged;
            MarkDirty();
        };
    }
    private void SourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => MarkDirty();
    private void MarkDirty() => IsDirty = true;

    public void Load(BackupJobSettings? job)
    {
        Name = job?.Name ?? "New backup";
        DestinationDirectory = job?.DestinationDirectory ?? "";
        ExclusionsText = string.Join(Environment.NewLine, job?.Exclusions ?? []);
        FullBackup = job?.FullBackup ?? false;
        foreach (var source in Sources) source.PropertyChanged -= SourceChanged;
        Sources.Clear();
        foreach (var source in job?.Sources ?? []) Sources.Add(new SourceEditor(source.Name, source.Directory));
        IsDirty = false;
    }
    public void AddSource(string path)
    {
        path = Path.GetFullPath(path);
        if (Sources.Any(source => string.Equals(Path.TrimEndingDirectorySeparator(source.Directory), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase))) return;
        var stem = new string(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        if (string.IsNullOrWhiteSpace(stem)) stem = "source";
        // Prefix avoids reserved Windows device names while retaining a familiar folder label.
        try { _ = new BackupPlan([new BackupSource(stem, path)], Path.GetTempPath()); }
        catch (ArgumentException) { stem = "folder-" + stem; }
        var alias = stem;
        for (var suffix = 2; Sources.Any(source => string.Equals(source.Name, alias, StringComparison.OrdinalIgnoreCase)); suffix++) alias = $"{stem}-{suffix}";
        Sources.Add(new SourceEditor(alias, path));
    }
    public BackupJobSettings ToSettings(Guid id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        var plan = new BackupPlan(Sources.Select(source => new BackupSource(source.Name.Trim(), Path.GetFullPath(source.Directory))),
            Path.GetFullPath(DestinationDirectory), ExclusionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return new BackupJobSettings { Id = id, Name = Name.Trim(), Sources = plan.Sources.ToArray(), DestinationDirectory = plan.DestinationDirectory,
            Exclusions = plan.Exclusions.ToArray(), FullBackup = FullBackup };
    }
}
