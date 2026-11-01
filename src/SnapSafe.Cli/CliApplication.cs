using SnapSafe.Core;
using System.Text.Json;

namespace SnapSafe.Cli;

public static class CliApplication
{
    private const string Usage = """
        SnapSafe - versioned directory backups

        backup --config <file.json> [--full]
        list --repository <directory>
        inspect --repository <directory> --snapshot <id>
        restore --repository <directory> --snapshot <id> --destination <directory>
                [--path <source-name/relative-path>]... [--on-conflict fail|skip|overwrite|rename]

        Configuration: {"sources":[{"name":"docs","directory":"./documents"}],"destinationDirectory":"./backups","exclusions":["**/bin/**","*.tmp"]}
        Configuration paths are relative to the JSON file. Source names become restore folders.
        Backup prints its snapshot ID. Omit --full to reuse unchanged files from previous snapshots.
        List shows completed snapshots newest first; inspect prints their JSON metadata and contents.
        Exclusions use named-source-relative paths, * and ? within components, and ** across directories.
        A basename pattern matches at any depth; a directory match excludes its subtree.
        Restore keeps named source folders. Omit --path to restore everything; repeat it to select files/directories.
        Conflict policy defaults to fail. Skip keeps existing files; overwrite replaces verified files;
        rename keeps both using numbered '(restored N)' names. File/directory type conflicts fail.
        Restore destinations must be outside the backup repository. Older snapshots remain available after source deletion.
        Use --help for this help. Press Ctrl+C to cancel safely.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length == 0 || (args.Length == 1 && args[0] is "--help" or "-h") ||
                (args.Length == 2 && args[0] is "backup" or "restore" or "list" or "inspect" && args[1] is "--help" or "-h"))
            {
                await output.WriteLineAsync(Usage);
                return 0;
            }
            string[] required = args[0] switch
            {
                "backup" => ["--config"],
                "list" => ["--repository"],
                "inspect" => ["--repository", "--snapshot"],
                "restore" => ["--repository", "--snapshot", "--destination"],
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
            var options = ParseOptions(args, required);
            string Value(string key) => options[key][0];
            var engine = new BackupEngine();
            if (args[0] == "backup")
            {
                var plan = await BackupPlan.LoadAsync(Value("--config"), cancellationToken);
                var result = await engine.CreateSnapshotAsync(plan, options.ContainsKey("--full"), cancellationToken: cancellationToken);
                await output.WriteLineAsync($"Snapshot: {result.Snapshot.Id}");
                await output.WriteLineAsync($"Files: {result.CopiedFiles} copied, {result.ReusedFiles} reused, {result.Snapshot.Files.Length} total.");
            }
            else if (args[0] == "restore")
            {
                var policy = options.ContainsKey("--on-conflict") ? Value("--on-conflict") switch
                {
                    "fail" => RestoreConflictPolicy.Fail,
                    "skip" => RestoreConflictPolicy.Skip,
                    "overwrite" => RestoreConflictPolicy.Overwrite,
                    "rename" => RestoreConflictPolicy.Rename,
                    _ => throw new ArgumentException("Invalid conflict policy. Use fail, skip, overwrite or rename.")
                } : RestoreConflictPolicy.Fail;
                var result = await engine.RestoreAsync(Value("--repository"), Value("--snapshot"), Value("--destination"),
                    new RestoreOptions { Paths = options.TryGetValue("--path", out var paths) ? paths.ToArray() : [], ConflictPolicy = policy }, cancellationToken);
                await output.WriteLineAsync($"Restored snapshot {Value("--snapshot")} to {Path.GetFullPath(Value("--destination"))}");
                await output.WriteLineAsync($"Files: {result.RestoredFiles} restored, {result.SkippedFiles} skipped, {result.RenamedFiles.Count} renamed.");
                foreach (var renamed in result.RenamedFiles)
                    await output.WriteLineAsync($"Renamed: {renamed.OriginalPath} -> {renamed.RestoredPath}");
            }
            else if (args[0] == "list")
            {
                var snapshots = await engine.ListSnapshotsAsync(Value("--repository"), cancellationToken);
                await output.WriteLineAsync("Snapshot ID\tCreated (UTC)\tFiles\tBytes\tComplete");
                foreach (var snapshot in snapshots)
                    await output.WriteLineAsync($"{snapshot.Id}\t{snapshot.CreatedAtUtc.UtcDateTime:O}\t{snapshot.FileCount}\t{snapshot.TotalBytes}\t{snapshot.Complete}");
                if (snapshots.Count == 0)
                    await output.WriteLineAsync("No completed snapshots.");
            }
            else
            {
                var snapshot = await engine.GetSnapshotAsync(Value("--repository"), Value("--snapshot"), cancellationToken);
                await output.WriteLineAsync(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Operation cancelled. Completed snapshots are preserved; retry the operation to continue.");
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

    private static Dictionary<string, List<string>> ParseOptions(string[] args, string[] required)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (options.ContainsKey(key) && key != "--path")
                throw new ArgumentException($"Duplicate option: {key}");
            if (key == "--full" && args[0] == "backup")
                options.Add(key, ["true"]);
            else if (required.Contains(key, StringComparer.Ordinal) || (args[0] == "restore" && key is "--path" or "--on-conflict"))
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {key}.");
                if (!options.TryGetValue(key, out var values))
                    options.Add(key, values = []);
                values.Add(args[i]);
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
