using System.Security.Cryptography;

namespace SnapSafe.Core;

internal static class FileIntegrity
{
    internal static FileStream OpenRead(string path)
    {
        PathSafety.EnsureNoLinks(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    internal static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));

    internal static async Task VerifyAsync(string path, SnapshotFile expected, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(path);
        if (stream.Length != expected.Length ||
            !string.Equals(await HashAsync(stream, cancellationToken), expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Backup content failed SHA-256 verification: {expected.RelativePath}");
    }

    internal static async Task CopyVerifiedAsync(Stream input, string destination, SnapshotFile expected, CancellationToken cancellationToken)
    {
        PathSafety.EnsureNoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous))
        {
            input.Position = 0;
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        await VerifyAsync(destination, expected, cancellationToken);
    }
}
