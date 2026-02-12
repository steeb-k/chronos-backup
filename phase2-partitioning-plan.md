# Phase 2 — Partition Manager Feature Plan

## Overview

Add a dedicated **Partitioning** page to the Chronos sidebar that provides a fully interactive, point-and-click partition manager. Users will be able to select a disk, visualize its partition layout (building on the existing `DiskMapControl`), and perform operations (delete, resize, create, format, convert MBR↔GPT) through right-click context menus and toolbar buttons. All changes are staged in a pending-operations queue and previewed before being committed in a single atomic pass.

> **Hard requirement:** Every dependency — native API or NuGet package — must fully support **x64 and ARM64** on Windows 10 19041+. No x64-only libraries.

---

## 1  What Already Exists (Reusable)

| Component | Location | Reuse |
|-----------|----------|-------|
| `DiskMapControl` (partition visualization) | `src/Views/DiskMapControl.xaml(.cs)` | Extend into an interactive version |
| `DiskInfo` / `PartitionInfo` models | `src/Chronos.Core/Models/DiskInfo.cs` | Extend with mutable "planned" state |
| `DiskEnumerator` (WMI + IOCTL enumeration) | `src/Chronos.Core/Services/DiskEnumerator.cs` | Reuse directly |
| `DiskApi` (P/Invoke: `GetDriveLayout`, `CreateFile`, `DeviceIoControl`) | `src/Chronos.Native/Win32/DiskApi.cs` | Extend with write IOCTLs |
| `VolumeApi` (volume GUID enumeration, extents) | `src/Chronos.Native/Win32/VolumeApi.cs` | Reuse for volume resolution |
| `DiskPreparationService` (dismount, lock, offline) | `src/Chronos.Core/Services/DiskPreparationService.cs` | Reuse for pre-commit preparation |
| `NavigationService` + sidebar | `src/MainWindow.xaml`, `NavigationService.cs` | Add new page tag |
| DI container | `src/App.xaml.cs` | Register new services + ViewModel |
| Operation history infrastructure | `IOperationHistoryService` | Log partition operations |

---

## 2  New Native APIs Required (`Chronos.Native`)

All of the following are Windows kernel32/ntdll IOCTLs available on **both x64 and ARM64** — no third-party native binaries needed.

### 2.1  Partition Table Write / Delete

| IOCTL | Purpose |
|-------|---------|
| `IOCTL_DISK_CREATE_DISK` (`0x7C058`) | Initialize a raw disk as MBR or GPT |
| `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` (`0x7C054`) | Write a complete partition table (MBR or GPT) |
| `IOCTL_DISK_DELETE_DRIVE_LAYOUT` (`0x7C100`) | Wipe the partition table entirely |
| `IOCTL_DISK_UPDATE_PROPERTIES` (already declared) | Force kernel to re-read partition table after changes |

**New structs needed:**

```
CREATE_DISK (union of CREATE_DISK_MBR / CREATE_DISK_GPT)
DRIVE_LAYOUT_INFORMATION_EX (variable-length, array of PARTITION_INFORMATION_EX)
SET_PARTITION_INFORMATION_EX
```

### 2.2  Volume Resize

| API | Purpose |
|-----|---------|
| `FSCTL_SHRINK_VOLUME` | Shrink an NTFS volume in-place (relocates clusters, then truncates) |
| `FSCTL_EXTEND_VOLUME` | Extend an NTFS volume into adjacent free space |
| `FSCTL_QUERY_ALLOCATED_RANGES` (already wired) | Determine movable data boundaries before shrink |

### 2.3  Volume Format

| Approach | Notes |
|----------|-------|
| WMI `Win32_Volume.Format()` | Managed, works on ARM64, supports NTFS/FAT32/exFAT/ReFS |
| `FormatEx` from `fmifs.dll` | Lower-level, also ARM64-safe, gives progress callbacks |

Prefer the WMI route first (already using `System.Management`); fall back to `fmifs.dll` only if progress reporting is needed.

### 2.4  MBR ↔ GPT Conversion (Data-Preserving)

Windows has no single IOCTL for in-place conversion. The implementation must:

1. **Read** the current layout via `IOCTL_DISK_GET_DRIVE_LAYOUT_EX`.
2. **Validate** constraints (MBR→GPT: ≤ 4 primary partitions, no extended/logical; GPT→MBR: ≤ 4 partitions, disk < 2 TiB, no partitions beyond 2 TiB).
3. **Build** the equivalent target layout, mapping MBR type bytes → GPT type GUIDs (or vice-versa).
4. **Write** the new table via `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` — partition offsets/lengths remain identical, so data is untouched.
5. **Call** `IOCTL_DISK_UPDATE_PROPERTIES` to force a rescan.

> This is the same technique `mbr2gpt.exe` uses internally. Because it only rewrites the descriptor table and not the partition contents, data is preserved. The protective MBR / backup GPT headers are handled by the IOCTL itself.

---

## 3  Architecture

### 3.1  Layer Diagram

```
┌─────────────────────────────────────────────────┐
│                 PartitionPage.xaml               │  ← UI (WinUI 3)
│  InteractiveDiskMapControl  ·  OperationQueue   │
└────────────────────┬────────────────────────────┘
                     │ data-binding
┌────────────────────▼────────────────────────────┐
│            PartitionViewModel                    │  ← ViewModel (MVVM)
│  PendingOps list · Preview state · Validation    │
└────────────────────┬────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────┐
│           IPartitionService                      │  ← Core service
│  CreatePartition · DeletePartition               │
│  ResizePartition · FormatVolume                  │
│  ConvertDiskStyle · ApplyPendingOperations       │
└────────────────────┬────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────┐
│        Chronos.Native  (P/Invoke)                │  ← Native interop
│  DiskApi (extended) · VolumeApi · FormatApi       │
└──────────────────────────────────────────────────┘
```

### 3.2  Pending Operations Model (Non-Destructive Preview)

Every user action creates a `PendingOperation` object instead of immediately modifying disk state:

```csharp
public enum PartitionOpType
{
    Create,
    Delete,
    Resize,       // shrink or extend
    Format,
    SetLabel,
    SetDriveLetter,
    ConvertDiskStyle,   // MBR ↔ GPT
    SetActive,          // MBR only — toggle active/boot flag
    ChangeTypeGuid,     // GPT only — change partition type GUID
}

public record PendingOperation(
    PartitionOpType Type,
    uint DiskNumber,
    int? PartitionNumber,       // null for Create / ConvertDiskStyle
    PartitionOperationParams Params,
    string DisplayDescription   // human-readable summary for queue UI
);
```

The ViewModel maintains a `List<PendingOperation>` and recomputes a **projected layout** after each mutation — this is what the `InteractiveDiskMapControl` renders during preview. Nothing touches the disk until the user clicks **Apply**.

### 3.3  Apply Pipeline

```
1.  Validate full op list (conflict detection, space checks)
2.  Create safety snapshot if possible (backup partition table to sidecar)
3.  DiskPreparationService — dismount affected volumes, lock, take offline
4.  Execute operations in dependency order:
      a.  Deletes (free space first)
      b.  Shrinks (release space)
      c.  Moves / Creates
      d.  Extends
      e.  Formats
      f.  Style conversions (MBR ↔ GPT)
5.  IOCTL_DISK_UPDATE_PROPERTIES
6.  Bring disk online, reassign drive letters
7.  Log to OperationHistory
```

Each step reports progress to the UI.

---

## 4  UI Design

### 4.1  Page Layout

```
┌──────────────────────────────────────────────────────────┐
│  [Disk selector dropdown]              [Refresh button]  │
├──────────────────────────────────────────────────────────┤
│                                                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │         InteractiveDiskMapControl                  │  │
│  │  ┌──────┬──────────────┬───────┬─────────────────┐ │  │
│  │  │ EFI  │   Windows    │ Recov │   Unallocated   │ │  │
│  │  │100MB │   200 GB     │ 800MB │    50 GB        │ │  │
│  │  └──────┴──────────────┴───────┴─────────────────┘ │  │
│  │        ↕ drag handles between partitions           │  │
│  └────────────────────────────────────────────────────┘  │
│                                                          │
│  ┌─ Partition Details ─────────────────────────────────┐ │
│  │  Label: Windows  ·  NTFS  ·  C:  ·  200 GB         │ │
│  │  Used: 85 GB  ·  Free: 115 GB  ·  GPT Type: Basic  │ │
│  └─────────────────────────────────────────────────────┘ │
│                                                          │
│  ┌─ Pending Operations ────────────────────────────────┐ │
│  │  1. Delete partition 4 (Recovery)                   │ │
│  │  2. Extend partition 2 (Windows) by 50 GB     [x]  │ │
│  │  ────────────────────────────────────────────────── │ │
│  │  [Clear All]                     [Apply Changes]    │ │
│  └─────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

### 4.2  InteractiveDiskMapControl (extends DiskMapControl)

Inherits from or wraps the existing `DiskMapControl` and adds:

| Feature | Implementation |
|---------|---------------|
| **Click to select** | Highlight selected partition, populate details pane |
| **Right-click context menu** | `MenuFlyout` with: Delete, Resize, Format, Set Label, Change Drive Letter, Set Active (MBR), Properties |
| **Right-click on unallocated** | Create New Partition (size, filesystem, label, drive letter) |
| **Right-click on disk header** | Convert to GPT / Convert to MBR, Initialize Disk, Properties |
| **Drag-to-resize handles** | Thin draggable splitters between adjacent partition/unallocated regions; snaps to MiB alignment |
| **Visual diff overlay** | During preview, changed partitions show a dashed border or a subtle color shift so the user can see what will change |
| **Tooltip on hover** | Quick stats: label, filesystem, size, used/free |

### 4.3  Confirmation Dialog

Before applying, show a modal `ContentDialog`:

- Lists every pending operation in plain English.
- Shows a before/after comparison (two `DiskMapControl` instances stacked).
- Bold red warning for destructive operations (delete, format).
- "I understand data may be lost" checkbox for destructive sets.
- **Apply** / **Cancel** buttons.

### 4.4  Sidebar Entry

Insert between **Mount** and **History** in `MainWindow.xaml`:

```xml
<NavigationViewItem Content="Partitioning" Tag="partitioning">
    <NavigationViewItem.Icon>
        <FontIcon Glyph="&#xECA5;" />  <!-- or a suitable Segoe Fluent icon -->
    </NavigationViewItem.Icon>
</NavigationViewItem>
```

---

## 5  Feature Matrix

### 5.1  Core (MVP)

| Feature | Details |
|---------|---------|
| **View all disks & partitions** | Dropdown lists all physical disks; selecting one renders the interactive map |
| **Select partition** | Click to select, view details pane |
| **Delete partition** | Right-click → Delete; queues operation |
| **Create partition in unallocated space** | Right-click unallocated → New Partition; wizard for size, filesystem, label, drive letter |
| **Resize (shrink/extend) NTFS partitions** | Right-click → Resize, or drag handles; respects data boundaries |
| **Format partition** | Right-click → Format; NTFS, FAT32, exFAT, ReFS options |
| **Convert MBR ↔ GPT (data-preserving)** | Right-click disk header → Convert; validates constraints first |
| **Pending operations queue with preview** | See all staged changes before committing |
| **Apply with confirmation dialog** | Before/after visual diff, destructive-op warning |
| **Operation logging** | Record applied changes in the History page |

### 5.2  Extended Features (Post-MVP)

| Feature | Details |
|---------|---------|
| **Change drive letter** | Right-click → Change Drive Letter; uses `DefineDosDevice` + volume GUID mount |
| **Set volume label** | Right-click → Set Label; calls `SetVolumeLabel` |
| **Wipe partition / secure erase** | Zero-fill or random-fill a partition before delete (reuse `DiskWriter` byte-writing) |
| **Align partition to optimal boundary** | Auto-suggest 1 MiB alignment for new partitions; warn on misalignment |
| **Clone partition** | Copy a partition to unallocated space on the same or different disk (reuse `BackupEngine` sector copy) |
| **Move partition** | Relocate a partition to a new offset (copy data → delete old → create at new offset) |
| **Partition table backup / restore** | Export the `DRIVE_LAYOUT_INFORMATION_EX` blob to a `.ptable` JSON sidecar; reimport later |
| **Initialize raw disk** | Detect uninitialized disks, offer GPT or MBR initialization |
| **Set active partition (MBR)** | Toggle the bootable flag on MBR disks |
| **Change GPT type GUID** | Reclassify a partition (e.g., Basic Data ↔ Linux Filesystem ↔ Recovery) |
| **VHD/VHDX partitioning** | Mount a virtual disk and partition it the same way (reuse `VirtualDiskInterop`) |
| **Disk health indicators** | Surface S.M.A.R.T. data alongside the partition map (WMI `MSFT_PhysicalDisk` or `Win32_DiskDrive`) |
| **USB / removable-safe mode** | Detect hot-plug disks; warn before modifying; prevent accidental system-disk operations |
| **Undo last apply** | Roll back the most recent commit by restoring the backed-up partition table sidecar |
| **Keyboard shortcuts** | `Del` to queue delete, `F2` for rename/label, `Ctrl+Z` to undo pending op |

---

## 6  Implementation Plan

> **A note on timelines and how we actually work.**
>
> These estimates assume our real workflow: Claude generates code in bulk, you integrate it, hit compile errors, debug WinUI quirks, and come back with what broke. The "writing" is fast; the **integration, debugging, and getting-it-right** is where the real time goes. Previous phases have taught us that skipping verification checkpoints creates compounding rework. This plan builds in explicit stop-and-verify gates to avoid that.

### Phase A — Native Layer & Core Service

**Writing: ~1 day | Integration & debugging: 2–3 days | Total: 3–4 days**

IOCTL struct marshalling is where ARM64 alignment bugs hide. Variable-length `DRIVE_LAYOUT_INFORMATION_EX` is notoriously tricky to marshal correctly. Budget time for trial-and-error here.

| Step | Task |
|------|------|
| A-1 | Add `IOCTL_DISK_CREATE_DISK`, `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`, `IOCTL_DISK_DELETE_DRIVE_LAYOUT` constants and P/Invoke wrappers to `DiskApi.cs` |
| A-2 | Add native structs: `CREATE_DISK`, `CREATE_DISK_MBR`, `CREATE_DISK_GPT`, `DRIVE_LAYOUT_INFORMATION_EX` (variable-length marshalling) |
| A-3 | Add `FSCTL_SHRINK_VOLUME` / `FSCTL_EXTEND_VOLUME` wrappers to `VolumeApi.cs` |
| A-4 | Add `FormatApi.cs` — WMI `Win32_Volume.Format()` wrapper (NTFS/FAT32/exFAT/ReFS) |
| A-5 | Create `IPartitionService` interface in `Chronos.Core` with all operation methods |
| A-6 | Implement `PartitionService` — orchestrates native calls, validates preconditions, supports dry-run (preview) mode |
| A-7 | Implement `MbrGptConverter` helper — reads layout, validates, builds target layout, writes |
| A-8 | Unit-test `PartitionService` validation logic against mock layouts |

> **⛳ Checkpoint A:** Before moving on, the project must build on both x64 and ARM64. Create a throwaway VHD and run `IOCTL_DISK_SET_DRIVE_LAYOUT_EX` round-trip (read layout → write identical layout → verify nothing changed). If this doesn't work, everything downstream is broken.

### Phase B — Data Models & ViewModel

**Writing: ~half a day | Integration: 1 day | Total: 1–2 days**

This is the most straightforward phase — pure C# classes and MVVM plumbing.

| Step | Task |
|------|------|
| B-1 | Define `PendingOperation`, `PartitionOperationParams`, and related enums in `Chronos.Core/Models/` |
| B-2 | Add projected-layout computation: take a `List<PartitionInfo>` + `List<PendingOperation>` → produce a hypothetical `List<PartitionInfo>` for preview |
| B-3 | Create `PartitionViewModel` — disk selection, partition selection, pending ops, apply command, undo command |
| B-4 | Register `PartitionViewModel` and `IPartitionService` in DI (`App.xaml.cs`) |

> **⛳ Checkpoint B:** Write a unit test that creates a fake 3-partition layout, queues a delete + create + resize, and asserts the projected layout is correct. This validates the preview logic before we build any UI on top of it.

### Phase C — Interactive UI

**Writing: ~2 days | Integration & WinUI debugging: 4–5 days | Total: ~1 week**

This is the phase that will fight back the hardest. WinUI drag/resize hit-testing is poorly documented, `Grid` column manipulation at runtime is fragile, and `MenuFlyout` context menus have subtle lifecycle bugs. Plan for multiple rounds of "it works in theory but not when you click it."

| Step | Task |
|------|------|
| C-1 | Create `InteractiveDiskMapControl` — clone `DiskMapControl`, add click selection, context menus, hover tooltips |
| C-2 | Implement drag-to-resize handles (thin `GridSplitter`-style elements between columns) |
| C-3 | Build `PartitionPage.xaml` — disk dropdown, interactive map, details pane, pending-ops list |
| C-4 | Wire context-menu commands (Delete, Resize, Format, Create, Convert) to ViewModel |
| C-5 | Build Create Partition dialog (`ContentDialog` with size slider, filesystem picker, label, drive letter) |
| C-6 | Build Resize Partition dialog (min/max bounds, slider or numeric input, MiB-aligned) |
| C-7 | Build Format Partition dialog (filesystem, label, quick/full toggle) |
| C-8 | Build Apply Confirmation dialog (before/after diff, destructive warning, checkbox) |
| C-9 | Add `partitioning` tag to sidebar in `MainWindow.xaml` and page map in `NavigationService.cs` |

> **⛳ Checkpoint C1 (after C-1 through C-3):** The page loads, enumerates disks, and renders the interactive map. You can click a partition and see details. Right-click shows a context menu (commands can be no-ops). **Stop here and verify before building dialogs.** If the map rendering or selection is broken, dialogs built on top of it are wasted work.

> **⛳ Checkpoint C2 (after C-4 through C-8):** Queue 2–3 operations via context menu, see them in the pending list, see the preview update in the map. Clear all. This validates the full UI loop before we wire it to real disk writes.

### Phase D — Safety, Hardening & Testing

**Writing: ~1 day | Testing & hardening: 3–4 days | Total: 4–5 days**

This is the phase we should **not** rush. A partition manager that silently corrupts a disk is worse than no partition manager. Every shortcut here is a potential data-loss bug report.

| Step | Task |
|------|------|
| D-1 | System-disk protection — detect and warn/block modifications to the running OS disk |
| D-2 | Partition table backup before apply — save layout to `.ptable.json` sidecar |
| D-3 | Progress reporting during apply (per-operation progress bar, cancel support where safe) |
| D-4 | Error handling — friendly messages for common failures (access denied, device busy, I/O errors) |
| D-5 | Power management — `ES_DISPLAY_REQUIRED` during apply (reuse existing pattern from backup/restore) |
| D-6 | Integration testing with VHD loopback disks (create a test VHD, partition it, verify layouts) |
| D-7 | ARM64 CI validation — ensure build + tests pass on ARM64 runner |

> **⛳ Checkpoint D:** Full end-to-end test on a VHD: create GPT → add 3 partitions → format one NTFS → shrink it → extend adjacent → delete one → convert to MBR → convert back to GPT. All on a disposable virtual disk, never a physical one. Must pass on both x64 and ARM64.

### Total Realistic Timeline

| Phase | Estimated Total |
|-------|----------------|
| A — Native + Core | 3–4 days |
| B — Models + ViewModel | 1–2 days |
| C — Interactive UI | ~1 week |
| D — Safety + Testing | 4–5 days |
| **Total** | **~2.5–3 weeks** |

This assumes roughly one working session per day. If we're doing marathon sessions, it could compress, but the debugging time doesn't compress as much as the writing time.

---

## 7  Quality Gates

These are hard requirements — if a gate fails, we stop and fix before moving forward. Skipping gates is how we end up spending 3 days untangling something that should have been caught in 10 minutes.

### Gate 1: Build Verification (after every phase)

- [ ] `dotnet build` succeeds for **all three platforms** (x86, x64, ARM64) with zero warnings treated as errors
- [ ] No new analyzer/nullable warnings introduced
- [ ] Existing tests still pass

### Gate 2: Native API Round-Trip (end of Phase A)

- [ ] Create a 1 GB VHD via `VirtualDiskInterop`
- [ ] Initialize as GPT via `IOCTL_DISK_CREATE_DISK`
- [ ] Read layout via `IOCTL_DISK_GET_DRIVE_LAYOUT_EX`
- [ ] Write identical layout back via `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`
- [ ] Re-read and assert layouts match byte-for-byte
- [ ] Repeat on ARM64 if available, otherwise flag for manual testing

### Gate 3: Preview Integrity (end of Phase B)

- [ ] Projected layout calculator has unit tests covering: single delete, create in unallocated, shrink + extend, multi-op chains, edge cases (delete last partition, fill all space)
- [ ] Projected layout never produces overlapping partitions or negative sizes
- [ ] Pending ops can be added, removed, reordered, and cleared without state corruption

### Gate 4: UI Smoke Test (Checkpoint C1)

- [ ] Page loads without exceptions
- [ ] Disk dropdown populates with real disks
- [ ] Partition map renders correctly for at least 2 different disks
- [ ] Click selection highlights the correct partition
- [ ] Right-click shows context menu without crash
- [ ] No visual glitches at different window sizes (test at 1280×720 and 1920×1080 minimum)

### Gate 5: Non-Destructive Operations Test (Checkpoint C2)

- [ ] Queue 5+ mixed operations; pending list displays correctly
- [ ] Preview map updates accurately after each queued operation
- [ ] "Clear All" resets to actual disk state
- [ ] Removing a single pending op mid-queue correctly recomputes the preview
- [ ] Confirmation dialog displays accurate before/after comparison

### Gate 6: Destructive Operations Test (end of Phase D)

- [ ] All operations execute correctly on a **VHD only** — never on a physical disk during testing
- [ ] System disk is correctly identified and protected (cannot queue destructive ops on it)
- [ ] Partition table `.ptable.json` backup is created before every apply
- [ ] After apply, `IOCTL_DISK_UPDATE_PROPERTIES` is called and Windows recognizes the new layout
- [ ] Friendly error messages appear for: access denied, device busy, I/O error, insufficient space
- [ ] Display stays on during apply (`ES_DISPLAY_REQUIRED`)

### Gate 7: ARM64 Verification (before merge/release)

- [ ] Full build passes on ARM64
- [ ] If an ARM64 device is available: manual smoke test (open page, enumerate disks, view partitions)
- [ ] No `BadImageFormatException` or marshalling errors on ARM64
- [ ] All struct sizes match expected values (write a test that asserts `Marshal.SizeOf<T>()` for each IOCTL struct)

---

## 8  Dependency Analysis (ARM64 Safety)

| Dependency | ARM64 Status | Notes |
|------------|-------------|-------|
| Windows IOCTLs (`DeviceIoControl`) | ✅ Native | Kernel API, arch-agnostic |
| WMI (`System.Management`) | ✅ Managed | Already in use; pure .NET, no native deps |
| `fmifs.dll` (`FormatEx`) | ✅ System DLL | Ships with Windows on all architectures |
| `CommunityToolkit.Mvvm` | ✅ Managed | Pure .NET source generators |
| `Microsoft.WindowsAppSDK` | ✅ Multi-arch | Already building for ARM64 |
| `Serilog` | ✅ Managed | No native dependencies |
| **No new NuGet packages required** | — | The entire feature is built on Windows kernel APIs already available on ARM64 |

> **Key takeaway:** This feature requires **zero new NuGet packages**. All disk manipulation is done through Windows IOCTLs and WMI, which are architecture-neutral. The UI is pure WinUI 3 (already ARM64-proven in our build pipeline).

---

## 9  Risk Mitigation

| Risk | Mitigation |
|------|-----------|
| **Data loss from bugs** | Pending-ops preview with before/after diff; partition table backup sidecar before every apply; system-disk protection; destructive-op confirmation with checkbox |
| **Incomplete NTFS shrink** (data at end of volume) | Query `FSCTL_QUERY_ALLOCATED_RANGES` to find maximum shrinkable extent; respect the filesystem's reported minimum size; show the user the actual achievable size, not just total free space |
| **MBR→GPT conversion fails** (Extended/Logical partitions) | Pre-validate: reject conversion if disk has extended partitions; show clear error message explaining why |
| **Device disconnected mid-apply** | Wrap each IOCTL call in retry/timeout logic with friendly Win32 error mapping (reuse existing I/O error handler); on fatal failure, attempt to restore backed-up partition table |
| **Concurrent access** (another tool modifying the disk) | Lock and dismount volumes before any write operation (reuse `DiskPreparationService`); take disk offline during multi-step operations |
| **UAC / admin elevation** | Disk IOCTLs require admin; Chronos already runs elevated — no change needed |

---

## 10  File Inventory (New & Modified)

### New Files

| File | Layer |
|------|-------|
| `src/Chronos.Native/Win32/PartitionApi.cs` | P/Invoke wrappers for partition write IOCTLs |
| `src/Chronos.Native/Win32/FormatApi.cs` | WMI volume format wrapper |
| `src/Chronos.Native/Structures/PartitionStructures.cs` | `CREATE_DISK`, `DRIVE_LAYOUT_INFORMATION_EX`, `SHRINK_VOLUME_INFORMATION` structs |
| `src/Chronos.Core/Models/PendingOperation.cs` | Operation queue data model |
| `src/Chronos.Core/Services/IPartitionService.cs` | Service interface |
| `src/Chronos.Core/Services/PartitionService.cs` | Service implementation |
| `src/Chronos.Core/Services/MbrGptConverter.cs` | MBR ↔ GPT conversion logic |
| `src/Chronos.Core/Services/PartitionLayoutProjector.cs` | Projected layout calculator for preview |
| `src/ViewModels/PartitionViewModel.cs` | ViewModel |
| `src/Views/PartitionPage.xaml` | Page XAML |
| `src/Views/PartitionPage.xaml.cs` | Page code-behind |
| `src/Views/InteractiveDiskMapControl.xaml` | Interactive partition map XAML |
| `src/Views/InteractiveDiskMapControl.xaml.cs` | Interactive partition map code-behind |
| `src/Views/Dialogs/CreatePartitionDialog.xaml(.cs)` | Create partition dialog |
| `src/Views/Dialogs/ResizePartitionDialog.xaml(.cs)` | Resize partition dialog |
| `src/Views/Dialogs/FormatPartitionDialog.xaml(.cs)` | Format partition dialog |
| `src/Views/Dialogs/ApplyChangesDialog.xaml(.cs)` | Confirmation dialog with diff |
| `tests/Chronos.Core.Tests/Services/PartitionServiceTests.cs` | Unit tests |
| `tests/Chronos.Core.Tests/Services/MbrGptConverterTests.cs` | Conversion unit tests |
| `tests/Chronos.Core.Tests/Services/PartitionLayoutProjectorTests.cs` | Projector unit tests |

### Modified Files

| File | Change |
|------|--------|
| `src/Chronos.Native/Win32/DiskApi.cs` | Add `IOCTL_DISK_CREATE_DISK`, `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`, `IOCTL_DISK_DELETE_DRIVE_LAYOUT`; helper method for `UpdateDiskProperties` |
| `src/Chronos.Native/Win32/VolumeApi.cs` | Add `FSCTL_SHRINK_VOLUME`, `FSCTL_EXTEND_VOLUME` wrappers |
| `src/Chronos.Core/Models/DiskInfo.cs` | Add optional `IsUninitialized` flag to `DiskInfo` |
| `src/MainWindow.xaml` | Add Partitioning `NavigationViewItem` |
| `src/Chronos.App/Services/NavigationService.cs` | Add `"partitioning" → PartitionPage` mapping |
| `src/App.xaml.cs` | Register `IPartitionService`, `PartitionViewModel` in DI |

---

## 11  Success Criteria

- [ ] User can select any physical disk and see an interactive partition map
- [ ] Right-click on a partition surfaces Delete, Resize, Format, and Properties options
- [ ] Right-click on unallocated space surfaces Create New Partition
- [ ] All changes are queued and visually previewed before committing
- [ ] Delete, create, shrink, extend, and format operations execute correctly
- [ ] MBR → GPT conversion succeeds on eligible disks without data loss
- [ ] GPT → MBR conversion succeeds on eligible disks without data loss
- [ ] System disk is protected from accidental modification
- [ ] Partition table is backed up before every apply
- [ ] All builds pass on x64 and ARM64
- [ ] Feature is accessible from the sidebar and integrates with History logging
