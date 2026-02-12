# Phase 2: Incremental Backups via VHDX Differencing Disks

## Overview

Leverage the VHDX format's native differencing disk support to implement incremental backups. Each incremental creates a child VHDX with `ParentPath` pointing to the previous backup. Change detection is done by comparing allocated sectors on the source disk against the parent VHDX — only sectors that differ are written to the child. For restore, Windows auto-resolves the differencing chain when the latest child is attached, presenting a merged view that feeds directly into the existing `RestoreEngine`. Chain management (merge, prune, validate) uses `MergeVirtualDisk` from `virtdisk.dll`.

## Change Detection Approach

**Sector comparison against the parent VHDX** — reliable, requires no external dependencies (no USN Journal wrapping risk, no RCT/Hyper-V requirement), and plays naturally with the differencing disk model by treating the parent VHDX as the baseline. The cost is reading both source and parent for allocated sectors, but since allocated-ranges scanning already skips free space, it's bounded by used capacity, not disk capacity.

**Why not alternatives:**

| Approach | Rejected Because |
|----------|-----------------|
| NTFS USN Journal | Risk of journal wrap (missed changes), requires complex file-to-sector mapping |
| Resilient Change Tracking (RCT) | Requires Hyper-V, not available on all Windows SKUs |
| Allocated-ranges diff only | Catches new/moved allocations but misses in-place file modifications |
| Block hashing alone | Same read cost as sector comparison but adds CPU overhead for hashing |

## Implementation Steps

### 1. Add New `virtdisk.dll` P/Invokes

**File:** `src/Chronos.Native/VirtDisk/VirtualDiskInterop.cs`

Add:
- `MergeVirtualDisk` — merge child into parent (for chain consolidation)
- `GetVirtualDiskInformation` — query parent path, virtual size, VHD type (for chain validation and UI)
- `SetVirtualDiskInformation` — update parent path (re-parenting after file moves)

Corresponding structs:
- `MergeVirtualDiskParameters` (V1: `MergeDepth`; V2: `MergeSourceDepth`, `MergeTargetDepth`)
- `GetVirtualDiskInfoVersion` enum
- `SetVirtualDiskInfoVersion` enum

### 2. Extend `BackupType` Enum

**File:** `src/Chronos.Core/Models/BackupJob.cs`

- Add `IncrementalDisk` and `IncrementalPartition` values to `BackupType`
- Add fields to `BackupJob`:
  - `ParentBackupPath` — path to the previous VHDX in the chain
  - `ChainId` — GUID identifying the backup chain

### 3. Extend `ImageSidecar` Metadata

**File:** `src/Chronos.Core/Models/ImageSidecar.cs`

Add fields:
- `BackupKind` — Full / Incremental / Differential
- `ParentImagePath`
- `ChainId`
- `SequenceNumber`
- `ChangedSectorCount`
- `ChangedBytesTotal`

This lets any sidecar file self-describe its position in a chain without needing the parent attached.

### 4. Add Differencing VHDX Support to `VirtualDiskService`

**File:** `src/Chronos.Core/VirtualDisk/VirtualDiskService.cs`

New methods:
- `CreateDifferencingVhdxAsync(path, parentPath)` — mirrors `CreateAndAttachVhdxForWriteAsync` but sets `ParentPath = Marshal.StringToHGlobalUni(parentVhdxPath)` and `MaximumSize = 0` (inherited from parent)
- `MergeVhdxAsync(childPath, mergeDepth)` — calls `MergeVirtualDisk`
- `GetVhdxParentPathAsync(path)` — calls `GetVirtualDiskInformation`

Update `IVirtualDiskService` interface accordingly.

### 5. Create `SectorComparer` Service

**New file:** `src/Chronos.Core/Imaging/SectorComparer.cs`

Given a source `DiskReadHandle` and a parent VHDX `DiskReadHandle`, iterates over allocated ranges and yields only ranges where sectors differ. Uses the same 2 MB buffer chunking as `BackupEngine`.

This is the change detection engine — it reads from both the live disk (via VSS snapshot) and the attached parent VHDX, compares buffers, and produces a `List<AllocatedRange>` of changed regions.

### 6. Add Incremental Path in `BackupEngine`

**File:** `src/Chronos.Core/Imaging/BackupEngine.cs`

At the routing branch (~L67): add case for `IncrementalDisk` / `IncrementalPartition` → `ExecuteIncrementalBackupAsync`.

New method `ExecuteIncrementalBackupAsync`:
1. Attach parent VHDX read-only via `AttachVhdxReadOnlyAsync`
2. VSS snapshot the source disk
3. Get allocated ranges from source
4. Run `SectorComparer` to narrow ranges to only changed sectors
5. Create differencing VHDX (child) via `CreateDifferencingVhdxAsync`
6. Write only changed sectors to the child using existing `CopySectorsWithRangesAsync`
7. Save extended sidecar with chain metadata
8. Detach parent VHDX

### 7. Handle Restore from Incremental Chains

**File:** `src/Chronos.Core/Imaging/RestoreEngine.cs`

- When the source VHDX is a differencing disk: Windows auto-resolves the parent chain on `AttachVhdxReadOnlyAsync` (provided all parents are in place). The attached disk presents the fully merged view. The _existing_ `RestoreFromVhdxAsync` should work unmodified against the merged view.
- Add chain validation before restore: walk the sidecar chain to verify all parent VHDXs exist and are intact before attempting attach. Surface clear errors if a parent is missing/moved.
- For "restore to a specific point in time": user picks which VHDX in the chain to attach (that VHDX + its ancestors form the restore point).

### 8. Create `BackupChainService`

**New file:** `src/Chronos.Core/Services/BackupChainService.cs`

Methods:
- `DiscoverChainAsync(vhdxPath)` — reads sidecar files to build the chain tree, validates parent paths
- `ValidateChainIntegrityAsync(chainId)` — ensures all VHDXs in the chain exist, parent paths resolve, sizes are consistent
- `MergeAsync(childPath, depth)` — calls `VirtualDiskService.MergeVhdxAsync` to consolidate children into parents. After merge, updates/removes sidecars for merged children.
- `RepairParentPathAsync(childPath, newParentPath)` — calls `SetVirtualDiskInformation` to fix broken parent references after file moves
- `PruneChainAsync(chainId, keepCount)` — merge old incrementals to keep the chain from growing indefinitely (e.g., keep last N incrementals, merge older ones into the base)

### 9. UI Integration

Extend `BackupViewModel` to:
- Offer "Incremental" as a backup type when a previous full backup exists for the selected source disk
- Show chain visualization (base → inc1 → inc2 → ...) in the restore UI with per-link size and timestamp
- Provide merge/prune controls in a chain management view
- Warn when a chain is getting long (e.g., >10 incrementals without merge)

### 10. Edge Cases and Safety

- **Parent VHDX must be read-only** — after an incremental is created, the parent must not be modified. Set file attributes or add a sidecar flag marking it as a chain parent.
- **Moved/renamed files** — if user moves VHDXs, parent paths break. `RepairParentPathAsync` and the chain discovery service handle this.
- **Source disk changed size** — if partitions are resized between backups, detect this in the sidecar comparison and fall back to a full backup with a clear message.
- **Corrupted chain member** — chain validation before restore, with clear error messages identifying which file is missing/corrupt.

## Verification

- **Unit tests** for `SectorComparer` with mock `DiskReadHandle`s (identical buffers → no output, differing buffers → correct ranges)
- **Integration test:** full backup → modify source files → incremental backup → attach child VHDX → verify merged view contains modified files
- **Chain merge test:** create base + 3 incrementals → merge depth 2 → verify base now contains all data
- **Chain validation test:** rename/delete a parent VHDX → verify chain validation catches it
- **Restore from incremental:** full backup → incremental → restore from child → verify restored disk matches source

## Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Change detection | Sector comparison vs parent VHDX | No journal-wrap risk, no file-to-sector mapping, no Hyper-V dependency. Bounded by used space, not total disk size. |
| Storage format | VHDX differencing disks | Windows-native parent/child resolution, `MergeVirtualDisk` provides consolidation, avoids inventing a container format |
| Chain metadata | Sidecar JSON files | Each VHDX's `.chronos.json` sidecar stores chain position, avoiding a separate database. Chain discovery rebuilds the tree by walking sidecars. |

## Existing Infrastructure Leveraged

- `CreateVirtualDiskParametersV2.ParentPath` — already declared as `IntPtr`, just needs to be set
- `CreateVirtualDiskFlags.UseChangeTrackingSourceLimit` / `PreserveParentChangeTrackingState` — already declared
- `OpenVirtualDiskFlags.CustomDiffChain` — already declared
- `VirtualStorageType.VHDSet` — already declared
- `AllocatedRangesProvider` — allocated-range scanning reused for incremental range building
- `CopySectorsWithRangesAsync` — sparse sector copy loop reused for writing only changed sectors
- `AttachVhdxReadOnlyAsync` — auto-resolves differencing chains on attach, making restore work with no changes
