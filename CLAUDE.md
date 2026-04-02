# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Chronos is a Windows disk imaging utility (WinUI 3 / .NET 10) supporting backup, restore, clone, verify, and VHDX mount operations with Zstandard compression, VSS snapshot support, and 4K-native sector handling. It runs on x86, x64, and ARM64, including in Windows PE environments.

## Build & Run

```powershell
# Debug build (defaults to x64)
dotnet build Chronos.sln -p:Platform=x64
dotnet build Chronos.sln -p:Platform=ARM64

# Run debug
dotnet run --project src/Chronos.App.csproj -p:Platform=x64

# Self-contained release publish
dotnet publish src/Chronos.App.csproj -c Release -r win-x64 --self-contained -p:Platform=x64

# Full release (installers + portable ZIPs, requires Inno Setup 6+)
.\scripts\Build-Release.ps1 -Version "1.0.0"
```

## Tests

```powershell
dotnet test
dotnet test tests/Chronos.Core.Tests/
dotnet test --filter "FullyQualifiedName~Chronos.Core.Tests.BackupEngineTests.ExecuteAsync_ValidJob"
```

Test frameworks: xUnit + Moq. All I/O tests require **Administrator privileges**.

## Architecture

Four projects with strict layering (no upward dependencies):

```
Chronos.App     — WinUI 3 / MVVM presentation layer
Chronos.Core    — Imaging engines, disk I/O, VSS, compression, virtual disks
Chronos.Native  — P/Invoke wrappers for Win32 / VirtDisk APIs
Chronos.Common  — PeEnvironment detection, shared utilities
```

**Key flows:**
- `BackupOperationsService` (App) orchestrates operations by calling `BackupEngine` / `RestoreEngine` / `VerificationEngine` (Core)
- Engines use `DiskReader` / `DiskWriter` (Core.Disk) which call into `Chronos.Native` P/Invokes
- `DiskEnumerator` uses WMI normally; falls back to IOCTL when `PeCapabilities.HasWmi` is false
- `VssService` wraps VSS COM interfaces; callers must check `IsVssAvailable()` before use
- `ZstdCompressionProvider` implements `ICompressionProvider` and is injected into the backup pipeline
- Progress flows through `IProgressReporter` injected into every async engine method

**MVVM pattern:**
- ViewModels inherit `ObservableObject`, use `[ObservableProperty]` / `[RelayCommand]`
- Global WinUI using aliases live in `src/Imports.cs`
- Private injected fields use `_camelCase`; all other fields use `PascalCase` (no underscore prefix for non-injected fields)

## WinPE Compatibility

Chronos auto-detects PE via `PeEnvironment` (registry keys `WinPE`/`MiniNT`, `startnet.cmd`, `winpeshl.exe`) and adapts:

| Feature | WinPE behavior |
|---|---|
| Mica/Acrylic backdrop | Disabled (avoids DWM compositor crash) |
| Disk enumeration | Falls back from WMI to IOCTL |
| VSS snapshots | Unavailable |
| Update checks | Skipped |
| AppData directory | Portable fallback chain: `%LOCALAPPDATA%` → `[exe]\AppData` → `X:\Chronos` → `%TEMP%\Chronos` |

**Self-test mode** validates a PE deployment without launching the UI:
```powershell
Chronos.App.exe --selftest
```

PE deployment requires ~25 system DLLs injected from a full Windows image. Use `scripts/Extract-WimDeps.ps1` and see `README-WinPE.md` for the complete list.

## Failure Cleanup and Operation History

**Partial-file cleanup on backup failure**: `BackupEngine.CopyToVhdxAsync` and `CopyToFileAsync`
use a `catch when (!copySucceeded)` guard that deletes the partial destination file (VHDX or raw
image) before rethrowing. The VSS snapshot and VHDX handle are always released first via `using`/
`finally`, so the file is guaranteed to be detached before deletion. The sidecar `.chronos.json`
is written in `ExecuteBackupAsync` only after the copy method returns successfully — it is never
written for failed or cancelled backups.

**Operation history**: `BackupOperationsService.StartBackupAsync` always logs an entry to
`IOperationHistoryService` in its `finally` block with status `"Success"`, `"Cancelled"`, or
`"Failed"`. `RestoreViewModel.StartRestoreAsync` does the same — it tracks `historyStatus` and
`historyError` across all catch branches and logs a `"Restore"` entry in its `finally` block.
Clone operations are logged as `"Clone"` by `BackupOperationsService`.

## Code Style

- Nullable reference types enabled; use `ArgumentNullException.ThrowIfNull()` in constructors
- `async` methods always accept `CancellationToken cancellationToken = default` and `IProgressReporter? progressReporter = null`
- Library code uses `.ConfigureAwait(false)`
- Catch `OperationCanceledException` and rethrow; log `Marshal.GetLastWin32Error()` on Win32 failures
- Structured logging via Serilog: `Log.Information(...)`, `Log.Error(ex, ...)`
- XML doc comments (`<summary>`) on all public APIs

## Version Management

Version is centralized in `version.json`. Run `scripts/sync-version.ps1` to propagate changes to `.csproj` files and `Version.props`.

## Logs

`%LOCALAPPDATA%\Chronos\Logs\chronos-YYYYMMDD.log`
