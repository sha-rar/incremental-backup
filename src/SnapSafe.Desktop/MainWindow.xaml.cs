using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SnapSafe.Core;
using SnapSafe.Desktop.Model;

namespace SnapSafe.Desktop;

public partial class MainWindow : Window
{
    private readonly DesktopWorkspace workspace;
    private bool closeRequested, readyToClose;
    public MainWindow(DesktopWorkspace workspace)
    {
        this.workspace = workspace;
        InitializeComponent(); DataContext = workspace;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await workspace.InitializeAsync();
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (readyToClose) return;
        e.Cancel = true;
        if (closeRequested) return;
        closeRequested = true; IsEnabled = false;
        workspace.Cancel(); await workspace.WaitForIdleAsync();
        if (workspace.Editor.IsDirty)
        {
            var choice = MessageBox.Show(this, "Save changes to this backup job before closing?\nYes saves your changes. No discards edits. Cancel returns to SnapSafe.",
                "Unsaved backup job", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes) await workspace.SaveJobAsync();
            else if (choice == MessageBoxResult.No) workspace.DiscardEdits();
            if (choice == MessageBoxResult.Cancel || workspace.Editor.IsDirty)
            {
                closeRequested = false; IsEnabled = true; return;
            }
        }
        try { await workspace.SavePreferencesAsync(); }
        catch (Exception exception) { MessageBox.Show(this, $"Settings could not be saved: {exception.Message}", "SnapSafe", MessageBoxButton.OK, MessageBoxImage.Error); }
        readyToClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    private async void Job_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.Selector selector && selector.SelectedItem is JobCard job)
            await workspace.SelectJobAsync(job);
    }
    private async void Snapshot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is SnapshotSummary snapshot) await workspace.SelectSnapshotAsync(snapshot);
    }
    private void NewJob_Click(object sender, RoutedEventArgs e) { workspace.NewJob(); MainTabs.SelectedIndex = 1; }
    private async void SaveJob_Click(object sender, RoutedEventArgs e) => await workspace.SaveJobAsync();
    private async void Discard_Click(object sender, RoutedEventArgs e) { workspace.DiscardEdits(); await workspace.RefreshAsync(); }
    private async void RemoveJob_Click(object sender, RoutedEventArgs e) => await workspace.RemoveJobAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await workspace.RefreshAsync();
    private async void Backup_Click(object sender, RoutedEventArgs e) => await workspace.BackupAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => workspace.Cancel();
    private void AddSources_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose source folders", Multiselect = true };
        if (picker.ShowDialog(this) == true) foreach (var folder in picker.FolderNames) workspace.Editor.AddSource(folder);
    }
    private void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourceGrid.SelectedItem is SourceEditor source) workspace.Editor.Sources.Remove(source);
    }
    private void Destination_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose backup destination" };
        if (picker.ShowDialog(this) == true) workspace.Editor.DestinationDirectory = picker.FolderName;
    }
    private void RestoreDestination_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose restore destination" };
        if (picker.ShowDialog(this) == true) workspace.RestoreDirectory = picker.FolderName;
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var entry in workspace.Entries) entry.IsSelected = true; }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { foreach (var entry in workspace.Entries) entry.IsSelected = false; }
    private async void RestoreSelected_Click(object sender, RoutedEventArgs e) => await workspace.RestoreAsync(all: false);
    private async void RestoreAll_Click(object sender, RoutedEventArgs e) => await workspace.RestoreAsync(all: true);
}
