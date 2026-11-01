namespace SnapSafe.Core;

public sealed class BackupDestinationException(string message, Exception? inner = null) : IOException(message, inner);

public interface IDestinationProbe
{
    void EnsureAccessible(string destination);
    void EnsureCapacity(string destination, long requiredBytes);
}

public sealed class DestinationProbe : IDestinationProbe
{
    public void EnsureAccessible(string destination)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination))!;
            if (!Directory.Exists(root)) throw new BackupDestinationException($"Backup destination is unavailable: {root}. Reconnect the drive or network share.");
            var ancestor = Path.GetFullPath(destination);
            while (!Directory.Exists(ancestor))
                ancestor = Path.GetDirectoryName(ancestor) ?? throw new BackupDestinationException("Backup destination has no available parent directory.");
            PathSafety.EnsureNoLinks(ancestor);
            var probe = Path.Combine(ancestor, ".snapsafe-write-" + Guid.NewGuid().ToString("N"));
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0); stream.Flush();
        }
        catch (BackupDestinationException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new BackupDestinationException($"Backup destination is unavailable or not writable: {destination}. {exception.Message}", exception); }
    }
    public void EnsureCapacity(string destination, long requiredBytes)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!);
            if (!drive.IsReady) throw new BackupDestinationException($"Backup drive is not ready: {drive.Name}");
            var free = drive.AvailableFreeSpace;
            if (free < requiredBytes) throw new BackupDestinationException($"Insufficient space at {destination}: need at least {requiredBytes:N0} bytes; {free:N0} bytes available.");
        }
        catch (BackupDestinationException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new BackupDestinationException($"Cannot check backup destination capacity: {destination}. {exception.Message}", exception); }
    }
}
