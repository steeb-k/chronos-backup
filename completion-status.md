# Chronos — Completion Status

**Current version:** 0.3.0  
**Last updated:** February 11, 2026

Master checklist of all planned, in-progress, and completed features across all phases.

---

## Phase 1 — Core Imaging Engine ✅

*Status: Complete*

### Backup Engine
- [x] Full disk backup to VHDX
- [x] Partition backup to VHDX
- [x] Zstandard compression (configurable level)
- [x] VSS snapshot integration for live backups
- [x] Allocated-ranges-only copy (skip free space)
- [x] Image verification / hash computation
- [x] Sidecar metadata (`.chronos.json`) with disk geometry, partition layout, checksums

### Restore Engine
- [x] Restore from VHDX to disk
- [x] Restore from VHDX to partition
- [x] Pre-restore safety checks (size validation, system disk warning)
- [x] Progress reporting with cancellation support

### Clone
- [x] Disk-to-disk clone
- [x] Partition-to-partition clone
- [x] Direct sector copy (no compression, no intermediate file)

### Mount / Browse
- [x] Mount VHDX to drive letter (read-only and read-write)
- [x] Mount VHDX to folder
- [x] Auto-dismount on app exit
- [x] File extraction from mounted images

### UI — Pages & Navigation
- [x] WinUI 3 app shell with Mica backdrop
- [x] Sidebar navigation (NavigationView)
- [x] Backup page
- [x] Restore page
- [x] Clone page
- [x] Browse / Mount page
- [x] Verify page
- [x] History page
- [x] Options page
- [x] About section (developer link, GitHub link)
- [x] Dark / Light theme support

### UI — Controls
- [x] `DiskMapControl` — Disk Management-style partition visualization
- [x] Disk selector with partition list
- [x] Progress reporting (speed, ETA, bytes transferred)
- [x] Pause / Resume / Cancel support

### Models & Infrastructure
- [x] `DiskInfo` / `PartitionInfo` models
- [x] `DiskEnumerator` (WMI + IOCTL dual enumeration)
- [x] Unallocated space detection
- [x] GPT type GUID classification
- [x] `DiskPreparationService` (dismount, lock, offline)
- [x] `VirtualDiskService` (create, attach, detach VHDX)
- [x] Settings persistence (`ApplicationData.LocalSettings`)
- [x] Operation history (JSON log, last 100 operations)
- [x] Dependency injection throughout
- [x] Serilog file logging

### Native Interop (`Chronos.Native`)
- [x] `DiskApi` — `CreateFile`, `DeviceIoControl`, `ReadFile`, `WriteFile`, IOCTL wrappers
- [x] `VolumeApi` — volume enumeration, disk extents, NTFS volume data, allocated ranges
- [x] `VirtualDiskInterop` — `virtdisk.dll` P/Invoke (create, open, attach, detach)
- [x] All pure P/Invoke — no C++/CLI, ARM64-safe

### Build & Deployment
- [x] Multi-platform build (x86, x64, ARM64)
- [x] Inno Setup installer (x64, ARM64)
- [x] `Build-Release.ps1` script
- [x] Version sync (`version.json` → `Version.props`)

---

## v0.2.0 Patch ✅

*Status: Complete*

- [x] Friendly Win32 error messages (error 1117 IO_DEVICE, 1167 DEVICE_NOT_CONNECTED)
- [x] Incomplete backup detection in `CopySectorsWithRangesAsync`
- [x] Pre-verification size sanity check via sidecar `ExpectedAllocatedBytes`
- [x] Keep display on during operations (`ES_DISPLAY_REQUIRED`)
- [x] History page with navigation and empty state
- [x] About section with developer (KZNJK.net) and GitHub links

---

## Phase 2 — Advanced Features

*Status: Planned*

### Incremental Backups (VHDX Differencing Disks)
*Detailed plan: `phase2-incremental-plan.md`*

- [ ] `MergeVirtualDisk` / `GetVirtualDiskInformation` / `SetVirtualDiskInformation` P/Invoke
- [ ] `BackupType.IncrementalDisk` / `IncrementalPartition` enum values
- [ ] Extended sidecar metadata (chain ID, sequence number, parent path, changed sectors)
- [ ] Differencing VHDX creation (`CreateDifferencingVhdxAsync`)
- [ ] `SectorComparer` service (diff source disk vs parent VHDX)
- [ ] Incremental backup path in `BackupEngine`
- [ ] Restore from differencing chain (auto-resolved by Windows on attach)
- [ ] `BackupChainService` — discover, validate, merge, prune, repair chains
- [ ] UI: incremental option in backup page, chain visualization in restore page
- [ ] Chain merge/prune controls
- [ ] Parent VHDX protection (mark read-only after child is created)

### WinPE Compatibility (PhoenixPE)
- [x] Detect PE environment at startup (`PeEnvironment.IsWinPE`)
- [x] Self-contained .NET publish (or portable runtime bundling) — already done via `WindowsAppSDKSelfContained`
- [ ] WinUI 3 / Windows App SDK runtime availability in PE (bundle or fallback) — needs PE testing
- [x] IOCTL-only disk enumeration fallback when WMI is unavailable
- [x] Graceful handling of missing persistent storage (settings, history) — `PeEnvironment.GetAppDataDirectory()`
- [x] Graceful handling of missing network stack (update checks skipped in PE)
- [x] WinPE readiness diagnostic script (`scripts/Test-WinPE-Readiness.ps1`)
- [ ] Restore functional in PE — needs PE testing
- [ ] Partition manager functional in PE
- [ ] Verify functional in PE — needs PE testing
- [ ] Browse / Mount functional in PE — needs PE testing
- [ ] Backup functional in PE (if target storage available) — needs PE testing

### Partition Manager
*Detailed plan: `phase2-partitioning-plan.md`*

- [ ] `IOCTL_DISK_CREATE_DISK` P/Invoke
- [ ] `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` P/Invoke
- [ ] `IOCTL_DISK_DELETE_DRIVE_LAYOUT` P/Invoke
- [ ] `IOCTL_DISK_UPDATE_PROPERTIES` helper method
- [ ] `FSCTL_SHRINK_VOLUME` / `FSCTL_EXTEND_VOLUME` P/Invoke
- [ ] Volume format via WMI (`Win32_Volume.Format()`)
- [ ] Native structs (`CREATE_DISK`, `DRIVE_LAYOUT_INFORMATION_EX`, etc.)
- [ ] `IPartitionService` interface + implementation
- [ ] `MbrGptConverter` — data-preserving MBR ↔ GPT conversion
- [ ] `PartitionLayoutProjector` — compute projected layout from pending ops
- [ ] `PendingOperation` model and operation queue
- [ ] `PartitionViewModel`
- [ ] `InteractiveDiskMapControl` (click select, right-click context menus, drag resize)
- [ ] `PartitionPage.xaml` with disk selector, details pane, pending ops list
- [ ] Create Partition dialog
- [ ] Resize Partition dialog
- [ ] Format Partition dialog
- [ ] Apply Confirmation dialog (before/after diff, destructive warning)
- [ ] Sidebar entry + navigation registration
- [ ] System disk protection
- [ ] Partition table backup (`.ptable.json` sidecar)
- [ ] DI registration

### Backup Scheduling
- [ ] Windows Task Scheduler integration (COM interop)
- [ ] Schedule types: one-time, daily, weekly, monthly, on-event
- [ ] Background execution when app is closed
- [ ] Missed backup detection + catch-up on launch
- [ ] Schedule management UI

### Notifications
- [ ] Windows Toast Notifications (completion, failure, scheduled start, low disk space)
- [ ] Actionable toasts (view logs, dismiss)
- [ ] Per-event notification preferences in Options
- [ ] Email notifications via SMTP (stretch)

### Network Destination Support
- [ ] UNC path support for backup destinations
- [ ] Credential storage via Windows Credential Manager
- [ ] Connection validation + retry logic
- [ ] Network share browser in destination picker

### Performance
- [ ] Multi-threaded zstd compression (parallel chunk streams)
- [ ] I/O buffer tuning (profile first, then optimize)
- [ ] Read-ahead buffering for sequential reads

### Resumable Operations
- [ ] Periodic checkpoint saving during backup/restore
- [ ] Detect incomplete operations on startup
- [ ] Resume from sector-level checkpoint

---

## Phase 3 — Extended Capabilities

*Status: Skeleton*

### VM Integration (QEMU + WHPX)
*Detailed plan: `extra-phase-vm-plan.md`*

- [ ] QEMU binary management (download, verify, store in `%LocalAppData%\Chronos\qemu\`)
- [ ] WHPX acceleration detection
- [ ] OVMF firmware bundling (UEFI boot for GPT backups)
- [ ] Launch VHDX as bootable VM from Chronos
- [ ] VM configuration (RAM, CPU cores)
- [ ] VM page in sidebar with status / controls

### AES-256 Encryption
- [ ] Streaming AES-256-GCM or AES-256-CBC encryption layer
- [ ] Password and key-file support
- [ ] Key derivation (Argon2id or PBKDF2)
- [ ] Encryption metadata in sidecar (salt, IV, KDF params)
- [ ] Encrypt-on-write during backup, decrypt-on-read during restore/mount
- [ ] Compatible with incremental chains (per-VHDX encryption)

### Cross-Sector-Size Restore (512B ↔ 4K)
- [ ] GPT parser (protective MBR, header, entries, CRC32 validation)
- [ ] GPT writer with LBA translation
- [ ] Alignment validation for target sector boundaries
- [ ] NTFS boot sector `BytesPerSector` update

### Image Catalog Database
- [ ] SQLite catalog (`Microsoft.Data.Sqlite`)
- [ ] Track all backups, chains, locations, status
- [ ] Auto-discovery from configured directories
- [ ] Retention policies (keep last N, prune old incrementals)
- [ ] Catalog page with quick actions

### Cloud Storage Integration
- [ ] Azure Blob Storage support
- [ ] AWS S3 support
- [ ] OneDrive / Google Drive support (stretch)
- [ ] Stream-based / chunked / resumable uploads
- [ ] Cloud credential management (OAuth, access keys)

### Command-Line Interface
- [ ] `Chronos.CLI` project (references `Chronos.Core` + `Chronos.Native`)
- [ ] Commands: backup, restore, verify, mount, list-disks, list-images
- [ ] JSON output mode
- [ ] Proper exit codes

### Custom Recovery Media Builder
- [ ] Automated PE image creation (no manual PhoenixPE)
- [ ] Bundle Chronos + runtime + drivers into bootable USB/ISO
- [ ] Driver injection (potentially reuse `idiot` project logic)
- [ ] UEFI + Legacy BIOS boot support
- [ ] Branded minimal shell

### Block-Level Deduplication (Stretch)
- [ ] Content-addressed storage (SHA-256 per block)
- [ ] Shared block store across backup chains
- [ ] Reference counting + garbage collection
- [ ] Space savings reporting

---

## Summary

| Phase | Total | Done | Remaining |
|-------|-------|------|-----------|
| Phase 1 | 38 | 38 | 0 |
| v0.2.0 | 6 | 6 | 0 |
| Phase 2 | 46 | 0 | 46 |
| Phase 3 | 31 | 0 | 31 |
| **Total** | **121** | **44** | **77** |
