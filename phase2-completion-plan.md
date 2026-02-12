# Phase 2 Completion Plan

What remains to be built for Phase 2. Detailed sub-plans live in their own documents — this file is the master checklist.

---

## Already Complete (Phase 1 + v0.2.0)

These items from the original plan are **done** and no longer tracked here:

- ✅ All XAML pages (Backup, Restore, Clone, Browse, Verify, History, Options)
- ✅ All ViewModels wired and functional
- ✅ Operation history (backend + UI + navigation)
- ✅ Settings persistence
- ✅ Friendly Win32 error messages (I/O device, device disconnected)
- ✅ Incomplete backup detection
- ✅ Pre-verification size sanity check
- ✅ Power management during operations (`ES_DISPLAY_REQUIRED`)
- ✅ About section with developer/GitHub links

---

## Phase 2 Feature Areas

### 1. Incremental Backups via VHDX Differencing Disks

**Priority: High** | **Detailed plan:** `phase2-incremental-plan.md`

VHDX differencing disks for incremental backups with sector-level change detection against the parent VHDX. Chain management via `MergeVirtualDisk`. Restore auto-resolves differencing chains — existing `RestoreEngine` works unmodified.

### 2. WinPE Compatibility (PhoenixPE)

**Priority: High**

Make Chronos run inside a PhoenixPE-built WinPE environment for bare-metal restore and partitioning. We are **not** building our own PE image yet — just ensuring Chronos launches and functions correctly when someone drops it into a PhoenixPE build.

Key challenges:
- WinPE has no WinUI 3 / Windows App SDK runtime — need to detect PE environment and either bundle the runtime or fall back to a minimal UI
- Limited .NET runtime availability — may need self-contained publish or a portable .NET runtime
- No Windows Shell, no Explorer, no Start menu — Chronos must launch standalone
- No persistent storage for settings/history — detect and handle gracefully
- Subset of WMI providers available — verify `Win32_DiskDrive`, `Win32_DiskPartition`, `Win32_Volume` work in PE
- Network stack may or may not be initialized — handle missing network gracefully

Scoped features for PE mode:
- Restore (primary use case — bare-metal recovery)
- Partition manager (format, create, resize before restore)
- Verify (confirm image integrity before restoring)
- Browse/Mount (pull individual files from a backup)
- Backup should work if target storage is available (USB, network share)

Not needed in PE mode:
- Scheduling, notifications, history persistence
- Auto-update

### 3. Partition Manager

**Priority: High** | **Detailed plan:** `phase2-partitioning-plan.md`

Interactive partition manager sidebar page. Delete, create, resize, format partitions. MBR↔GPT data-preserving conversion. Pending operations queue with visual before/after preview.

### 4. Backup Scheduling

**Priority: Medium**

- Windows Task Scheduler integration via COM interop (ARM64-safe)
- Schedule types: one-time, daily, weekly, monthly, on-event (startup, logon)
- Background execution when app is closed
- Missed backup detection + catch-up on next launch
- Schedule management UI (sidebar page or Options sub-section)

### 5. Notifications

**Priority: Medium**

- Windows Toast Notifications for backup completion, failures, scheduled starts, low disk space
- Actionable toasts (view logs, dismiss)
- Notification preferences in Options (per-event toggle)
- Email notifications (SMTP) as a stretch goal — evaluate complexity vs. value

### 6. Network Destination Support

**Priority: Medium**

- UNC path support (`\\server\share\backups`) for backup destinations
- Credential storage via Windows Credential Manager (ARM64-safe, no extra packages)
- Connection validation before operations, retry logic on failure
- Browse network shares in destination picker

### 7. Performance

**Priority: Low-Medium**

- Multi-threaded zstd compression (chunk-based parallel streams, configurable thread count)
- I/O buffer tuning (profile current bottlenecks before optimizing)
- Read-ahead buffering for sequential disk reads

### 8. Resumable Operations

**Priority: Low**

- Periodic checkpoint saving during long backup/restore operations
- Detect incomplete operations on startup
- Resume from last sector-level checkpoint
- Particularly valuable for large disks over slow/unreliable connections (pairs with network support)

---

## Deferred to Phase 3+

These were in the original plan but are out of scope for Phase 2:

- Block-level deduplication (content-addressed storage)
- Cross-sector-size restore (GPT LBA translation for 512B↔4K)
- Building our own bootable PE image (Phase 3 — PhoenixPE compatibility is Phase 2)
- Cloud storage integration (Azure, AWS, OneDrive)
- AES-256 encryption
- Command-line interface
- Image catalog database (SQLite) — sidecar JSON files are sufficient for now

---

## Implementation Order

| Order | Feature | Depends On |
|-------|---------|-----------|
| 1 | WinPE compatibility | — (validate core features work in PE first) |
| 2 | Incremental backups | — |
| 3 | Partition manager | — (can parallel with #2; critical for PE use case) |
| 4 | Scheduling | Incremental (schedules often trigger incremental runs) |
| 5 | Notifications | Scheduling (main trigger for notifications) |
| 6 | Network destinations | — (independent, but pairs well with scheduling + PE) |
| 7 | Performance | Profile after #2–#3 are working |
| 8 | Resumable operations | Network destinations (main use case) |

---

## Success Criteria

- [ ] Chronos launches and runs core features (restore, partition, verify, browse) inside a PhoenixPE environment
- [ ] Incremental backup chains work end-to-end (full → incremental → restore from chain)
- [ ] Partition manager can delete, create, resize, format, and convert MBR↔GPT
- [ ] Scheduled backups execute via Windows Task Scheduler when app is closed
- [ ] Toast notifications fire on backup completion/failure
- [ ] UNC path destinations work with credential storage
- [ ] All features build and run on x64 and ARM64
- [ ] No new NuGet packages that lack ARM64 support
