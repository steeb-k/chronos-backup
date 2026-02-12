# Phase 1 Completion Plan

Plan for implementing the remaining Phase 1 features from `appPlan.md`. Pause/Resume and Mount-to-Folder have been dropped—Cancel alone is sufficient, and drive-letter mounts are sufficient for browse.

---

## Current Status

| Area | Status | Notes |
|------|--------|-------|
| Backup | ✅ Complete | Full disk/partition to VHDX, VSS, compression, progress, cancel |
| Disk/Partition Clone | ✅ Complete | Dedicated ClonePage with disk/partition picker |
| Restore | ✅ Complete | RestoreEngine with validation, progress, cancellation |
| Verification | ✅ Integrity + SHA-256 | Filesystem consistency checks remaining (Medium priority) |
| Browse/Mount | ✅ Complete | Mount with `NoSecurityDescriptor` for full folder access; Explorer-based extraction |
| Settings Persistence | ✅ Complete | JSON settings, Options page with all bindings |
| Operation History | ✅ Complete | Backend service + HistoryPage in footer nav |

---

## 1. Restore Engine

**Priority: High**

### 1.1 Create RestoreEngine Implementation

- Add `RestoreEngine.cs` implementing `IRestoreEngine`
- Register in DI (`App.xaml.cs`)
- Flow:
  1. Open source VHDX (attach or open read-only)
  2. Open target disk/partition for write via `IDiskWriter`
  3. Sector-by-sector copy from image to target
  4. Optional: verify during copy (read-back compare)

### 1.2 Pre-Restore Safety Checks (`ValidateRestoreAsync`)

- Confirm target is not the system/boot drive (or warn)
- Confirm target size ≥ image size
- Confirm user confirmation before destructive write

### 1.3 Wire RestoreViewModel

- Bind `StartRestoreAsync` to `IRestoreEngine.ExecuteAsync`
- Add progress reporting and cancellation
- Add validation before starting restore

---

## 2. Disk and Partition Clone

**Priority: High**

### 2.1 Clone vs Backup Logic

- **Backup**: Source = disk/partition, Destination = file (.vhdx / .zst)
- **Clone**: Source = disk/partition, Destination = physical disk/partition

### 2.2 Implementation Approach

- Extend `BackupEngine` (or add `CloneEngine`) to support destination type: file vs physical disk
- For disk clone: destination path = `\\.\PhysicalDriveN`
- For partition clone: destination = physical partition path
- Reuse existing sector copy logic; change only the destination handle acquisition

### 2.3 UI Changes

- Bind Backup type ComboBox to `SelectedBackupType`
- When Clone is selected: show disk/partition picker for destination instead of file path
- Validate source and destination are different disks/partitions

---

## 3. Browse/Mount

**Priority: Medium**

### 3.1 Mount to Drive Letter

- Use Virtual Disk API: `OpenVirtualDisk` + `AttachVirtualDisk` with `AttachVirtualDiskFlags.None` (assign drive letter)
- Or use `SetVolumeMountPoint` / `DefineDosDevice` after attach
- Find available drive letter (e.g. iterate `GetLogicalDriveStrings`, pick first free)

### 3.2 Dismount

- Call `DetachVirtualDisk` on the attached handle
- Track mounted VHDXs so we can detach on demand and on app exit

### 3.3 Automatic Dismount on Exit

- Maintain a list of VHDXs mounted by the app
- On `App.Suspending` or `MainWindow.Closed`, iterate and call `DismountAsync`

### 3.4 File Extraction

- After mount: use normal file APIs (`File.Copy`, `Directory.EnumerateFiles`) on the mounted drive
- Add "Extract to folder" flow in BrowseViewModel with destination picker

### 3.5 Wire BrowseViewModel

- Implement `BrowseImageAsync`, `MountToDriveLetterAsync`, `DismountAsync`
- Implement extraction command and UI

---

## 4. Verification Enhancements

**Priority: Medium**

### 4.1 Filesystem Consistency Checks

- If image is VHDX: attach read-only, run `chkdsk /scan` or equivalent
- Or: verify NTFS/ReFS structures (e.g. MFT) are readable
- Important for validating recoverability — not just integrity

---

## 5. UI/UX

**Priority: Medium**

### 5.1 Operation History Log

- Persist completed operations (backup, restore, verify) to a log file or SQLite
- Add History page or section showing: timestamp, operation type, source, destination, status
- Optional: link to detailed log file

### 5.2 Settings Persistence

- Use `ApplicationData.LocalSettings` or `ApplicationData.RoamingFolder` for options
- Persist: `DefaultCompressionLevel`, `DefaultBackupPath`, `UseVssByDefault`, `VerifyByDefault`, `UseDarkTheme`
- Load on app start, save when options change

---

## Suggested Implementation Order

1. **Restore Engine** — ✅ Complete
2. **Settings Persistence** — ✅ Complete
3. **Disk/Partition Clone** — ✅ Complete
4. **Mount to Drive Letter** — ✅ Complete (with NoSecurityDescriptor for full access)
5. **Dismount + Auto-dismount** — ✅ Complete
6. **Operation History** — ✅ Complete (service + UI page)
7. **File Extraction** — ✅ Resolved via Explorer-based extraction after security-bypass mount
8. **Filesystem Consistency** — Remaining (Medium priority)

---

## Files to Create or Modify

| Task | Create | Modify |
|------|--------|--------|
| Restore Engine | `Chronos.Core/Imaging/RestoreEngine.cs` | `App.xaml.cs`, `RestoreViewModel.cs` |
| Clone | — | `BackupEngine.cs` or new `CloneEngine.cs`, `BackupViewModel.cs`, `BackupPage.xaml` |
| Mount | Possibly `Chronos.Native/Win32/MountApi.cs` | `VirtualDiskService.cs`, `BrowseViewModel.cs` |
| Settings | — | `OptionsViewModel.cs`, `App.xaml.cs` |
| History | `Chronos.App/Services/OperationHistoryService.cs` | New History view or Options section |

---

## Architecture Notes

- All implementations should remain pure P/Invoke or managed; no C++/CLI or architecture-specific binaries
- Target x86, x64, and ARM64
- Virtual Disk API and VSS work across all supported architectures
