using System.Windows;
using System.Windows.Threading;
using SnapSafe.Desktop.Model;
using Xunit;

namespace SnapSafe.Desktop.Tests;

public class WindowSmokeTests
{
    [Fact]
    public void CorruptSettingsWindowClosesWithoutReenteringTheClosingEvent()
    {
        using var temp = new TestFolder();
        var store = new SettingsStore(temp.Write("settings.json", "{broken"));
        Exception? failure = null;
        Dispatcher? dispatcher = null;
        var closed = false;
        var stage = "starting STA thread";
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.UnhandledException += (_, args) =>
                {
                    failure = args.Exception; args.Handled = true;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                stage = "creating WPF application";
                var app = new SnapSafe.Desktop.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                var workspace = new DesktopWorkspace(store, new DesktopBackupService());
                stage = "creating window";
                var window = new SnapSafe.Desktop.MainWindow(workspace) { ShowInTaskbar = false, Opacity = 0 };
                window.Closed += (_, _) => { closed = true; dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
                stage = "showing window";
                window.Show();
                stage = "waiting for dispatcher callback";
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    stage = "waiting for settings load";
                    await workspace.WaitForIdleAsync();
                    stage = "closing window";
                    window.Close();
                }));
                Dispatcher.Run();
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var finished = thread.Join(TimeSpan.FromSeconds(15));
        if (!finished) dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        Assert.True(finished, $"Desktop window did not finish closing: {stage}.");
        Assert.Null(failure);
        Assert.True(closed);
        Assert.Equal("{broken", File.ReadAllText(store.FilePath));
    }
}
