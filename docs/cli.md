# SnapSafe command-line backup engine

Requires the .NET 10 SDK. From the repository root:

```powershell
dotnet build SnapSafe.slnx
dotnet test SnapSafe.slnx
dotnet run --project src/SnapSafe.Cli -- --help
```

Create a JSON job file (for example `job.json`):

```json
{
  "sources": [
    { "name": "documents", "directory": "C:/Users/you/Documents" },
    { "name": "projects", "directory": "C:/Projects" }
  ],
  "destinationDirectory": "D:/SnapSafeBackups"
}
```

Relative paths resolve against the configuration file's directory. Each source needs a unique, stable name; restored files are placed under that name. Source and repository paths must not overlap. The engine includes hidden files and empty directories. It explicitly rejects symbolic links and junctions, including those in ancestor directories, rather than traversing them.

Create the first full snapshot, then run the same command for incremental snapshots:

```powershell
dotnet run --project src/SnapSafe.Cli -- backup --config job.json
```

The command prints the completed snapshot's ID and copied/reused file counts. SHA-256 detects changes even when length and modification time are unchanged. Incremental snapshots refer directly to the earlier stored copy of each unchanged file. To copy every file again:

```powershell
dotnet run --project src/SnapSafe.Cli -- backup --config job.json --full
```

Restore a completed snapshot using its printed ID:

```powershell
dotnet run --project src/SnapSafe.Cli -- restore --repository D:/SnapSafeBackups --snapshot <snapshot-id> --destination C:/Recovered
```

The restore directory must be new or empty and must not overlap the repository. Restoring the example job produces `C:/Recovered/documents/...` and `C:/Recovered/projects/...`. SnapSafe verifies all referenced data before creating the restore tree and verifies each copied file before giving it its final name. Existing files are never overwritten. If restoration is cancelled, already restored files may remain; retry into a new or emptied directory.

Exit codes: `0` success/help, `1` filesystem, content-integrity or JSON error, `2` invalid arguments/configuration, `130` cancellation. Ctrl+C cancels the active operation.

## Repository layout and recovery

```text
<repository>/
  .lock
  snapshots/
    <snapshot-id>/
      manifest.json
      files/<source-name>/<relative-path>  # files copied by this snapshot only
    .pending-<snapshot-id>/                # unpublished work, if interrupted
```

Manifests describe the complete directory tree, file sizes, SHA-256 hashes and storage snapshot IDs. They are published with a same-volume directory rename only after all copied and reused data is verified. Both backup and restore acquire an exclusive repository lock. The `.lock` file can remain after exit; the open file handle is the lock, and the operating system releases it if the process exits.

A failed or cancelled backup never publishes its staging directory. The next backup removes stale staging left by process termination and retries the job. Existing completed snapshots are retained. Deleting a source file only removes it from subsequent snapshots; older snapshots still restore it. Do not delete or edit completed snapshot directories: later snapshots may reference their files. A forced full backup can create fresh copies if an earlier stored copy has been damaged; it does not repair older snapshots.

## Phase 1 limits

This is a file-level backup engine, not a filesystem image. It preserves file contents and directory structure, including empty directories. It does not preserve ACLs, alternate data streams, timestamps or other filesystem metadata. It does not use Volume Shadow Copy: a changing directory tree is not captured at one instant. A source file is opened without write sharing while hashing/copying; locked or inaccessible files fail the operation visibly. Protect the repository from external modification while operations are running.

Snapshot browsing, selective restore, exclusions, retention, scheduling and a desktop UI belong to later phases. No completed versions are pruned automatically. Filesystem power-loss durability depends on the storage device; staging publication addresses interrupted application operations.
