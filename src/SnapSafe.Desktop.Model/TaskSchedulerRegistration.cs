using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Security.Principal;

namespace SnapSafe.Desktop.Model;

public interface IAutomationRegistration
{
    Task SetEnabledAsync(bool enabled, string settingsPath, CancellationToken token);
}

public sealed class TaskSchedulerRegistration(string workerPath) : IAutomationRegistration
{
    public static XDocument CreateTaskDefinition(string workerPath, string settingsPath, string userSid)
    {
        workerPath = Path.GetFullPath(workerPath); settingsPath = Path.GetFullPath(settingsPath);
        if (workerPath.Contains('"') || settingsPath.Contains('"')) throw new ArgumentException("Invalid task action path.");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Element(string name, object value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.4"),
            Element("RegistrationInfo", Element("Description", "Runs enabled SnapSafe backup jobs at their configured intervals.")),
            Element("Triggers", Element("TimeTrigger", new object[] {
                Element("Repetition", new object[] { Element("Interval", "PT1M"), Element("StopAtDurationEnd", false) }),
                Element("StartBoundary", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")), Element("Enabled", true) })),
            Element("Principals", new XElement(ns + "Principal", new XAttribute("id", "User"),
                Element("UserId", userSid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
            Element("Settings", new object[] { Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", false),
                Element("StopIfGoingOnBatteries", false), Element("StartWhenAvailable", true), Element("Enabled", true), Element("Hidden", true), Element("ExecutionTimeLimit", "PT72H") }),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), Element("Exec", new object[] {
                Element("Command", workerPath), Element("Arguments", $"--settings \"{settingsPath}\" --once"), Element("WorkingDirectory", Path.GetDirectoryName(workerPath)!) }))));
    }
    public static string TaskName(string settingsPath) => "SnapSafe-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(settingsPath).ToUpperInvariant())))[..16];
    public async Task SetEnabledAsync(bool enabled, string settingsPath, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Automatic task registration requires Windows.");
        if (enabled && !File.Exists(workerPath)) throw new FileNotFoundException("The backup worker is missing. Build or install the complete SnapSafe application.", workerPath);
        string? xmlPath = null;
        try
        {
        if (enabled)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var definition = CreateTaskDefinition(workerPath, settingsPath, identity.User?.Value ?? throw new IOException("Current Windows user has no SID."));
            xmlPath = Path.GetTempFileName();
            await File.WriteAllTextAsync(xmlPath, definition.ToString(), Encoding.Unicode, token);
        }
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in enabled ? new[] { "/Create", "/TN", TaskName(settingsPath), "/XML", xmlPath!, "/F" } : new[] { "/Delete", "/TN", TaskName(settingsPath), "/F" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows Task Scheduler could not start.");
        var errors = process.StandardError.ReadToEndAsync(token); var output = process.StandardOutput.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var errorText = await errors; var outputText = await output;
        if (process.ExitCode != 0) throw new IOException($"Windows Task Scheduler rejected the change: {errorText} {outputText}");
        }
        finally { if (xmlPath is not null) File.Delete(xmlPath); }
    }
}
