namespace SnapSafe.Notifications;

public static class WindowsNotifications
{
    public static Task ShowAsync(string title, string message, bool error)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var icon = new NotifyIcon { Icon = System.Drawing.SystemIcons.Shield, Text = "SnapSafe", Visible = true };
                using var timer = new System.Windows.Forms.Timer { Interval = 6000 };
                timer.Tick += (_, _) => Application.ExitThread();
                timer.Start(); icon.ShowBalloonTip(5000, title, message.Length > 240 ? message[..240] : message, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
                Application.Run();
            }
            catch (Exception) { /* Notifications are best effort; job outcomes remain in the activity log. */ }
            finally { completion.TrySetResult(); }
        }) { IsBackground = true, Name = "SnapSafe notification" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
}
