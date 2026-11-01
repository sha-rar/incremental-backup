# Incremental Backup

SnapSafe (a working name) is a Windows desktop app for backing up folders and recovering earlier versions of your files. Choose what to protect, choose where to store it, and run backups yourself or on a schedule.

The first backup copies your files. Later backups copy new or changed files and reuse unchanged ones to save space. Each successful backup creates a **snapshot**: a version you can browse and restore, including files you have since changed or deleted.

The app is under development. There is no installer yet; you currently run it from this repository.

## Get started

You need Windows and the **.NET 10 SDK** installed. The SDK is needed to build the app; installing only the .NET runtime is not enough.

1. Download and extract this repository, or clone it with Git.
2. Open PowerShell in the repository folder.
3. Build and launch the desktop app:

```powershell
dotnet build SnapSafe.slnx --configuration Release
dotnet run --project src/SnapSafe.Desktop --configuration Release --no-build
```

After building, you can also launch `src\SnapSafe.Desktop\bin\Release\net10.0-windows\SnapSafe.Desktop.exe` directly. Keep its output folder together, including the worker and other supporting files.

## Make your first backup

1. Click **New job** and give it a name, such as “My documents.”
2. In **Backup jobs**, click **Add folders** to choose one or more source folders.
3. Choose a **Backup destination**. Use a dedicated folder, ideally on another drive or an external disk. It must be separate from your source folders, with neither location inside the other.
4. Leave the optional settings at their defaults to start. Click **Save job**, then **Back up now**.
5. Check the completion message. The **Dashboard** shows the latest backup, snapshot count, and storage used.

The **Restore folder name** beside each source determines its top-level folder when you recover files. For example, a source named `Documents` restores into `Documents` inside your chosen recovery folder.

You can save several jobs and switch between them using **Current job**. Save or discard edits before switching jobs or starting a backup. **Remove job** removes its configuration and keeps its existing backups.

### Skip files you do not need

Enter exclusion patterns in **Backup jobs**, one per line. For example:

```text
*.tmp
**/bin/**
**/obj/**
```

These skip temporary files and folders named `bin` or `obj`. Exclusions affect future backups; they do not remove files from earlier snapshots.

Leave **Make a full copy each time** unchecked for normal use. Enable it only if you want every backup to copy all included files again, using more storage.

## Back up automatically

In **Backup jobs**, enable **Run this job automatically while I am logged in, even with SnapSafe closed**, choose an interval in minutes, and click **Save job**. For example, `360` means every six hours and `1440` means once a day.

SnapSafe registers a task in Windows Task Scheduler. Automatic backups work with the app closed while you are logged in and the computer is awake. Leave the built app and its supporting files in place so the scheduled worker can run. Saving an enabled job makes it eligible for an automatic backup; you do not need to click **Back up now** first.

If a drive or network destination is unavailable, or there is insufficient space, the job reports the problem. Scheduled failures are retried automatically. Reconnect the destination or free space, then check **Activity**. You can also click **Back up now** to retry yourself.

To stop automatic backups for a job, clear its automatic-backup checkbox and save. If task setup fails, the app displays an error; click **Refresh** to retry setup.

## Choose how much history to keep

Retention is optional. In **Backup jobs**, leave both limits blank to keep all snapshots, or enter a maximum snapshot count, an age in days, or both. When both are set, snapshots must satisfy both limits. The newest snapshot is always kept.

Cleanup runs after a successful desktop or scheduled backup. It removes expired versions from history while preserving file data still needed by the snapshots you keep. Expired versions can no longer be restored. All jobs using the same destination must use the same retention settings.

Do not manually edit or delete files inside the backup destination. Snapshots can share stored data, so deleting one folder by hand could damage other versions.

## Restore your files

1. Select the job and open **History & restore**.
2. Choose a snapshot by its creation time. Times in the app are shown in UTC.
3. For a partial restore, check the files or folders you want. No selection is needed to restore everything.
4. Set **Restore to** to a separate recovery folder. A new, empty folder is the easiest starting point.
5. Choose what happens if files already exist, then click **Restore selected** or **Restore entire snapshot**.

| Conflict option | What happens |
| --- | --- |
| Fail | Stops if a file would conflict. This is the default. |
| Skip | Leaves existing files in place. |
| Overwrite | Replaces existing files with the backed-up versions. |
| Rename | Restores under another name so both copies are kept. |

SnapSafe verifies backed-up file contents during restoration. Restoring to a separate folder lets you check the recovered files before moving them back. If you cancel a restore, files already recovered may remain there.

## Check activity and settings

**Activity** shows recent manual and scheduled backup outcomes, including warnings and failures. Use **Refresh activity** to load new entries, and **Show desktop backup notifications** to control notifications.

Job settings are saved in `%LOCALAPPDATA%\SnapSafe\settings.json`; backup logs are stored in the `logs` folder beside it. Your backed-up files remain in the destination you chose.

## Current limitations

- SnapSafe backs up file contents and folder structure, including empty folders. It does not make a full Windows system image or preserve permissions, timestamps, or all other file metadata.
- Symbolic links and junctions are rejected. Locked or inaccessible files can cause a backup to fail; check the error and retry after closing the application using them.
- Backups are not an instant capture of an entire running system. Close applications that are actively changing important files before backing them up.
- Backups are not encrypted or compressed. Protect access to the destination as you would your original files.

## Command-line use

A command-line interface is also available for creating backups, listing and inspecting snapshots, and restoring files. To see its commands and options:

```powershell
dotnet run --project src/SnapSafe.Cli -- --help
```
