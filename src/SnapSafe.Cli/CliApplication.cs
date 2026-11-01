using SnapSafe.Core;
using System.Text.Json;

namespace SnapSafe.Cli;

public static class CliApplication
{
    private const string Usage = """
        SnapSafe - versioned directory backups

        backup --config <file.json> [--full]
        restore --repository <directory> --snapshot <id> --destination <empty-directory>

        Configuration: {"sources":[{"name":"docs","directory":"./documents"}],"destinationDirectory":"./backups"}
        Configuration paths are relative to the JSON file. Source names become restore folders.
        Backup prints its snapshot ID. Omit --full to reuse unchanged files from previous snapshots.
        Restore requires a new or empty directory outside the backup repository.
        Use --help for this help. Press Ctrl+C to cancel safely.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length == 0 || (args.Length == 1 && args[0] is "--help" or "-h") ||
                (args.Length == 2 && args[0] is "backup" or "restore" && args[1] is "--help" or "-h"))
            {
                await output.WriteLineAsync(Usage);
                return 0;
            }
            if (args[0] is not ("backup" or "restore"))
                throw new ArgumentException($"Unknown command: {args[0]}");
            var backup = args[0] == "backup";
            var required = backup ? new[] { "--config" } : ["--repository", "--snapshot", "--destination"];
            var options = ParseOptions(args, required, backup);
            var engine = new BackupEngine();
            if (backup)
            {
                var plan = await BackupPlan.LoadAsync(options["--config"], cancellationToken);
                var result = await engine.CreateSnapshotAsync(plan, options.ContainsKey("--full"), cancellationToken: cancellationToken);
                await output.WriteLineAsync($"Snapshot: {result.Snapshot.Id}");
                await output.WriteLineAsync($"Files: {result.CopiedFiles} copied, {result.ReusedFiles} reused, {result.Snapshot.Files.Length} total.");
            }
            else
            {
                await engine.RestoreAsync(options["--repository"], options["--snapshot"], options["--destination"], cancellationToken);
                await output.WriteLineAsync($"Restored snapshot {options["--snapshot"]} to {Path.GetFullPath(options["--destination"])}");
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Operation cancelled. An interrupted backup is not published; run backup again to retry.");
            return 130;
        }
        catch (ArgumentException exception)
        {
            await error.WriteLineAsync($"Error: {exception.Message}");
            await error.WriteLineAsync("Use --help for usage.");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            await error.WriteLineAsync($"Error: {exception.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args, string[] required, bool backup)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (options.ContainsKey(key))
                throw new ArgumentException($"Duplicate option: {key}");
            if (key == "--full" && backup)
                options.Add(key, "true");
            else if (required.Contains(key, StringComparer.Ordinal))
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {key}.");
                options.Add(key, args[i]);
            }
            else
                throw new ArgumentException($"Unknown option: {key}");
        }
        foreach (var key in required)
        {
            if (!options.ContainsKey(key))
                throw new ArgumentException($"Required option: {key}");
        }
        return options;
    }
}
