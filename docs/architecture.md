# Architecture notes

Proposed boundaries:

- `SnapSafe.Core`: job definitions, snapshot manifests, file traversal, comparison, copy, verification, and restore.
- `SnapSafe.Cli`: command parsing and progress/status output; no backup logic embedded here.
- `SnapSafe.Core.Tests`: tests over temporary folders and artificial failures.
- `SnapSafe.Desktop` (future): Windows WPF UI using the same core operations.

Initial snapshot strategy: file-level incremental backups with immutable manifests. Verify copies before marking snapshots complete. The design must preserve deleted-file recovery and treat restore as a first-class operation.
