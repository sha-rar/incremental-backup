# PowerShell 5.1+ or 7+, run from the directory that should contain the repository.
$ErrorActionPreference = "Stop"
$root = Join-Path (Get-Location) "incremental-backup"
if (Test-Path $root) { throw "Folder already exists: $root" }
New-Item -ItemType Directory -Path $root | Out-Null

$path = Join-Path $root '.gitignore'
$parent = Split-Path -Parent $path
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$content = @'
**/bin/
**/obj/
.vs/
*.user
*.suo
TestResults/
*.log
.env
'@
[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))

$path = Join-Path $root 'README.md'
$parent = Split-Path -Parent $path
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$content = @'
# SnapSafe — Incremental Backup Tool

A local-first desktop backup application that copies selected folders safely, keeps previous versions, and makes restoration straightforward.

**Status:** Starter scaffold. The CLI and core library compile, but backup, scheduling, verification, and restore operations are not implemented yet.

## The finished product

SnapSafe will let users choose important directories and a backup destination, schedule regular backups, and recover files from an earlier point in time without manually sorting through copies.

A user should be able to:

- Select source folders, choose a destination drive/folder, and configure exclusions.
- Run an initial full backup and then copy only files whose contents have changed.
- Preserve multiple recoverable versions rather than overwriting the only good copy.
- View backup history, elapsed time, data copied, skipped items, and errors.
- Restore one file, a folder, or a complete snapshot to a chosen location.
- Verify stored data using SHA-256 checksums and report missing or corrupt backup files.
- Schedule recurring jobs and resume sensibly after a missed run or unavailable destination.
- Use a Windows desktop interface, with the same operations available through a CLI.

Backups must remain distinct from synchronisation: deleting a source file should **not** automatically destroy its recoverable backups.

## Intended technology

- **C# / .NET:** filesystem operations, scheduling, core services, and CLI.
- **WPF:** eventual Windows desktop UI, developed after the backup engine is proven.
- **JSON manifests or SQLite:** snapshot metadata, job settings, and version history.
- **SHA-256:** integrity checking and optional duplicate detection.
- **xUnit:** automated tests for backup and restore behaviour.

Avoid building the desktop interface first: test the engine with temporary folders and deliberately interrupted backups before depending on it.

## Initial repository structure

```text
incremental-backup/
├── src/
│   ├── SnapSafe.Core/
│   │   ├── BackupPlan.cs        # Models and future backup logic
│   │   └── SnapSafe.Core.csproj
│   └── SnapSafe.Cli/
│       ├── Program.cs          # Initial CLI entry point
│       └── SnapSafe.Cli.csproj
├── tests/
│   └── SnapSafe.Core.Tests/
│       ├── BackupPlanTests.cs
│       └── SnapSafe.Core.Tests.csproj
├── docs/
│   └── architecture.md
├── .gitignore
└── README.md
```

A solution file and a `src/SnapSafe.Desktop/` WPF project can be added using the .NET SDK when needed. This directory layout is intentionally small and may change.

## Roadmap

1. **CLI and configuration:** Define backup jobs with source folders, destination folders, and excludes; validate paths.
2. **First backup:** Copy files to a timestamped snapshot and record a manifest. Preserve relative paths and metadata needed for restoration.
3. **Incremental operation:** Detect changes using file metadata and hashes where appropriate; avoid copying identical data repeatedly.
4. **Safe snapshot handling:** Write to temporary locations and commit manifests only after successful copies; recover from interruption without treating partial data as complete.
5. **Restore and verification:** Implement file and folder restore, checksum verification, missing-file reports, and automated tests using temporary directories.
6. **Scheduling and history:** Add recurring backup jobs, run logs, and sensible behaviour when the destination is disconnected.
7. **Desktop interface:** Add WPF screens for backup jobs, progress, snapshots, errors, and restoration.
8. **Packaging and validation:** Package for Windows, test large files and edge cases, document limitations and restore procedures.

## Set-up (Windows PowerShell)

Requires the .NET 10 SDK or another SDK capable of building the target framework.

```powershell
dotnet run --project .\src\SnapSafe.Cli\SnapSafe.Cli.csproj
dotnet test .\tests\SnapSafe.Core.Tests\SnapSafe.Core.Tests.csproj
```

These commands currently run a placeholder CLI and a starter unit test. They **do not** make backups.

## Reliability requirements

- Never delete or modify the user's original files during a normal backup operation.
- Never claim a partially written snapshot is complete.
- Restore to a separate destination by default; handle overwrites explicitly.
- Do not silently remove older backups; any retention policy must be opt-in and testable.
- Handle inaccessible files, permissions, symbolic links, path collisions, and disconnected destinations explicitly.
- Test restoration, not just copying. A backup isn't useful unless it can be recovered.

## Definition of done

A user can select a folder, create and schedule incremental snapshots, disconnect and reconnect the destination, verify the backup, and restore a known file version. Tests cover unchanged files, modifications, deletions, interruptions, and damaged backup data.

## Out of scope for the initial release

Cloud storage, cross-device synchronisation, encryption, and block-level deduplication. Start with dependable file-level backups and recoverability.
'@
[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))

$path = Join-Path $root 'docs\architecture.md'
$parent = Split-Path -Parent $path
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$content = @'
# Architecture notes

Proposed boundaries:

- `SnapSafe.Core`: job definitions, snapshot manifests, file traversal, comparison, copy, verification, and restore.
- `SnapSafe.Cli`: command parsing and progress/status output; no backup logic embedded here.
- `SnapSafe.Core.Tests`: tests over temporary folders and artificial failures.
- `SnapSafe.Desktop` (future): Windows WPF UI using the same core operations.

Initial snapshot strategy: file-level incremental backups with immutable manifests. Verify copies before marking snapshots complete. The design must preserve deleted-file recovery and treat restore as a first-class operation.
'@
[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))

$path = Join-Path $root 'src\SnapSafe.Cli\Program.cs'
$parent = Split-Path -Parent $path
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$content = @'
Console.WriteLine("SnapSafe scaffold ready.");
Console.WriteLine("TODO: implement backup, verify, restore, and scheduling commands.");
'@
[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))

$path = Join-Path $root 'src\SnapSafe.Cli\SnapSafe.Cli.csproj'
$parent = Split-Path -Parent $path
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$content = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../SnapSafe.Core/SnapSafe.Core.csproj" />
  </ItemGroup>
</Project>
'@
[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))

$path = Join-Path $root 'src\SnapSafe.Core\BackupPlan.cs'
$parent = Split-Path -Parent $path
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$content = @'
namespace SnapSafe.Core;

/// <summary>A backup job's source and destination (engine to be implemented).</summary>
public sealed record BackupPlan(string SourceDirectory, string DestinationDirectory);
'@
[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))

$path = Join-Path $root 'src\SnapSafe.Core\SnapSafe.Core.csproj'
$parent = Split-Path -Parent $path
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$content = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@
[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))

$path = Join-Path $root 'tests\SnapSafe.Core.Tests\BackupPlanTests.cs'
$parent = Split-Path -Parent $path
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$content = @'
using SnapSafe.Core;
using Xunit;

namespace SnapSafe.Core.Tests;

public class BackupPlanTests
{
    [Fact]
    public void BackupPlanKeepsSourceAndDestination()
    {
        var plan = new BackupPlan(@"C:\Source", @"D:\Backups");
        Assert.Equal(@"C:\Source", plan.SourceDirectory);
        Assert.Equal(@"D:\Backups", plan.DestinationDirectory);
    }
}
'@
[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))

$path = Join-Path $root 'tests\SnapSafe.Core.Tests\SnapSafe.Core.Tests.csproj'
$parent = Split-Path -Parent $path
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$content = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.1">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/SnapSafe.Core/SnapSafe.Core.csproj" />
  </ItemGroup>
</Project>
'@
[System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))

if (Get-Command git -ErrorAction SilentlyContinue) { git -C $root init }
Write-Host "Created $root"
Write-Host "Read its README.md for setup and milestones."
