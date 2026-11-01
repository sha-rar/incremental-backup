# Architecture notes

Proposed boundaries:

- `SnapSafe.Core`: job definitions, snapshot manifests, file traversal, comparison, copy, verification, and restore.
- `SnapSafe.Cli`: command parsing and progress/status output; no backup logic embedded here.
- `SnapSafe.Core.Tests`: tests over temporary folders and artificial failures.
- `SnapSafe.Desktop` (future): Windows WPF UI using the same core operations.

Phase 1 implements file-level incremental backups with immutable versioned JSON manifests. Each manifest describes a complete tree and refers directly to the snapshot storing each file's bytes. SHA-256 determines changes; unchanged contents are verified and reused without copying. Forced full backups copy all current files.

`BackupPlan` loads named source roots and a repository destination from JSON. `DirectoryScanner` discovers relative file paths and empty directories. `BackupEngine` coordinates backup and restoration, `FileIntegrity` copies/hashes/verifies bytes, and `SnapshotRepository` owns locking, manifest validation and staging recovery.

Snapshots are built under `.pending-<id>` and become visible through an atomic directory rename only after content verification and manifest flush. The repository lock serializes backup/restore operations. Retry removes abandoned staging; completed snapshots are never pruned. Old manifests and file data remain recoverable when sources change or disappear.

Restoration validates manifest paths and hashes before creating the output tree, and verifies copied contents before publishing each restored file. Targets must be new or empty and separate from the repository. Filesystem links are rejected explicitly in Phase 1. See [CLI usage and limitations](cli.md).
