using System.Security.Cryptography;
using System.Buffers;

namespace SnapSafe.Core;

internal static class FileIntegrity
{
    internal static async Task<SnapshotFile> CopyAndHashAsync(Stream input, string destination, string relativePath, string owner, CancellationToken token)
    {
        PathSafety.EnsureNoLinks(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var expectedLength = input.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0; input.Position = 0;
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), token)) != 0)
                {
                    hash.AppendData(buffer, 0, count); length += count;
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                await output.FlushAsync(token); output.Flush(flushToDisk: true);
            }
            if (length != expectedLength) throw new IOException($"Source changed while copying: {relativePath}");
            var entry = new SnapshotFile(relativePath, length, Convert.ToHexString(hash.GetHashAndReset()), owner);
            await VerifyAsync(destination, entry, token);
            return entry;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
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
