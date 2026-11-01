using System.Text.Json;
using SnapSafe.Core;
using SnapSafe.Desktop.Model;
using Xunit;

namespace SnapSafe.Desktop.Tests;

public class SettingsTests
{
    private static BackupJobSettings Job(TestFolder temp) => new()
    {
        Name = "Documents", Sources = [new BackupSource("docs", temp.At("input"))],
        DestinationDirectory = temp.At("backup"), Exclusions = ["*.tmp"], FullBackup = true
    };

    [Fact]
    public async Task SettingsRoundTripPreservesJobsSelectionAndRestorePreferences()
    {
        using var temp = new TestFolder();
        var store = new SettingsStore(temp.At("settings/settings.json"));
        var job = Job(temp);
        await store.SaveAsync(new DesktopSettings { Jobs = [job], SelectedJobId = job.Id,
            RestoreDirectory = temp.At("restore"), ConflictPolicy = RestoreConflictPolicy.Rename });
        var restored = await new SettingsStore(store.FilePath).LoadAsync();
        Assert.Equal(job.Id, restored.SelectedJobId);
        var saved = Assert.Single(restored.Jobs);
        Assert.Equal("Documents", saved.Name);
        Assert.Equal("docs", Assert.Single(saved.Sources).Name);
        Assert.Equal(job.DestinationDirectory, saved.DestinationDirectory);
        Assert.Equal(["*.tmp"], saved.Exclusions);
        Assert.True(saved.FullBackup);
        Assert.Equal(temp.At("restore"), restored.RestoreDirectory);
        Assert.Equal(RestoreConflictPolicy.Rename, restored.ConflictPolicy);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!));
    }

    [Fact]
    public async Task MissingSettingsStartEmptyWithoutWritingAFile()
    {
        using var temp = new TestFolder();
        var store = new SettingsStore(temp.At("missing/settings.json"));
        Assert.Empty((await store.LoadAsync()).Jobs);
        Assert.False(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"FormatVersion\":99,\"Jobs\":[]}")]
    [InlineData("{\"FormatVersion\":1,\"Jobs\":null}")]
    public async Task InvalidSettingsAreReportedAndOriginalFileIsPreserved(string contents)
    {
        using var temp = new TestFolder();
        var store = new SettingsStore(temp.Write("settings.json", contents));
        var error = await Record.ExceptionAsync(() => store.LoadAsync());
        Assert.True(error is JsonException or InvalidDataException);
        Assert.Equal(contents, await File.ReadAllTextAsync(store.FilePath));
    }

    [Fact]
    public async Task InvalidOrDuplicateJobsCannotReplaceSavedConfiguration()
    {
        using var temp = new TestFolder();
        var store = new SettingsStore(temp.At("settings.json"));
        var job = Job(temp);
        await store.SaveAsync(new DesktopSettings { Jobs = [job] });
        var original = await File.ReadAllTextAsync(store.FilePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new DesktopSettings { Jobs = [job, job] }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new DesktopSettings { Jobs = [job with { Sources = [] }] }));
        Assert.Equal(original, await File.ReadAllTextAsync(store.FilePath));
    }

    [Fact]
    public async Task CancelledSavePreservesPreviousSettings()
    {
        using var temp = new TestFolder();
        var store = new SettingsStore(temp.At("settings.json"));
        await store.SaveAsync(new DesktopSettings { Jobs = [Job(temp)] });
        var original = await File.ReadAllTextAsync(store.FilePath);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new DesktopSettings(), cancel.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(store.FilePath));
        Assert.Single(Directory.GetFiles(temp.Root));
    }
}
