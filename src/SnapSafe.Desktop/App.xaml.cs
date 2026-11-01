using System.IO;
using System.Windows;
using SnapSafe.Desktop.Model;

namespace SnapSafe.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapSafe", "settings.json");
        if (e.Args.Length != 0)
        {
            if (e.Args.Length != 2 || e.Args[0] != "--settings")
            {
                MessageBox.Show("Usage: SnapSafe.Desktop [--settings <file.json>]", "SnapSafe");
                Shutdown(2); return;
            }
            settingsPath = Path.GetFullPath(e.Args[1]);
        }
        var workspace = new DesktopWorkspace(new SettingsStore(settingsPath), new DesktopBackupService());
        MainWindow = new MainWindow(workspace);
        MainWindow.Show();
    }
}
