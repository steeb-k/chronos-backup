# Phase 3 Completion Plan (Skeleton)

Features deferred from Phase 2 plus new capabilities that build on the Phase 2 foundation.

---

## Feature Areas

### 1. VM Integration (QEMU + WHPX)

**Priority: High** | **Detailed plan:** `extra-phase-vm-plan.md`

Boot any VHDX backup as a VM directly from Chronos for verification and file recovery. WHPX hardware acceleration when available, software emulation fallback. Bundled QEMU binaries downloaded to `%LocalAppData%\Chronos\qemu\`.

### 2. AES-256 Encryption

**Priority: High**

- Encrypt VHDX backups at rest (AES-256-GCM or AES-256-CBC)
- Password or key-file based encryption
- Encrypt-on-write during backup, decrypt-on-read during restore/mount
- Streaming encryption layer that sits between the compression and disk-write stages
- Key derivation via Argon2id or PBKDF2 (both available in .NET without extra packages)
- Store encryption metadata (salt, IV, KDF params) in sidecar — never store the key
- Must not break incremental chain resolution (encrypt each VHDX independently)

### 3. Cross-Sector-Size Restore (512B ↔ 4K)

**Priority: Medium**

Currently blocked with a clear error message (Phase 1). Full implementation requires:

- GPT parser: read protective MBR, GPT header, partition entries, validate CRC32
- GPT writer with LBA translation: `new_lba = (old_lba * old_sector_size) / new_sector_size`
- Alignment validation: verify all partitions align to target sector boundaries
- NTFS boot sector `BytesPerSector` field update
- 4K → 512B is straightforward; 512B → 4K requires alignment verification

### 4. Image Catalog Database

**Priority: Medium**

Replace ad-hoc sidecar discovery with a lightweight SQLite catalog:

- `Microsoft.Data.Sqlite` (ARM64-safe, no native deps beyond the bundled SQLite)
- Track all backups, their locations, chain relationships, and status
- Auto-discovery: scan configured directories, import sidecar metadata
- Retention policies: keep last N full backups, prune old incrementals
- Quick actions: mount, verify, delete, restore from catalog view
- New sidebar page or sub-section of existing page

### 5. Cloud Storage Integration

**Priority: Medium**

- Azure Blob Storage, AWS S3, OneDrive/Google Drive as backup destinations
- Stream-based upload (no full local copy required for cloud targets)
- Chunked/resumable uploads for large VHDX files
- Credential management (OAuth for consumer clouds, access keys for Azure/S3)
- Evaluate ARM64-compatible SDKs: `Azure.Storage.Blobs` (managed, ARM64-safe), `AWSSDK.S3` (managed)
- May pair with encryption (encrypt before upload)

### 6. Command-Line Interface

**Priority: Medium**

- Headless mode for scripting and automation
- Core commands: backup, restore, verify, mount, list-disks, list-images
- JSON output mode for programmatic consumption
- Exit codes for success/failure/partial
- Could be a separate `Chronos.CLI` project referencing `Chronos.Core` and `Chronos.Native`
- Enables Task Scheduler integration without the full WinUI app

### 7. Custom Chronos Recovery Media Builder

**Priority: Low**

Build on the PhoenixPE compatibility work from Phase 2 to create a Chronos-branded PE image builder:

- Automate PE image creation (no manual PhoenixPE setup required)
- Bundle Chronos + runtime + drivers into a bootable USB/ISO
- Driver injection for target hardware (potentially reuse `idiot` project's logic)
- UEFI + Legacy BIOS boot support
- Minimal branded shell (launch Chronos on boot, no desktop)

### 8. Block-Level Deduplication

**Priority: Low (Stretch)**

- Content-addressed storage using SHA-256 per block
- Shared block store across multiple backup chains
- Reference counting for safe block deletion
- Background garbage collection
- Space savings reporting
- Significant architectural complexity — evaluate whether the storage savings justify it

---

## Deferred / Out of Scope

- Split archives for DVD/USB distribution
- Third-party API / plugin system
- Linux/macOS support

---

## Dependencies on Phase 2

| Phase 3 Feature | Requires from Phase 2 |
|-----------------|----------------------|
| VM Integration | Working VHDX backups (Phase 1, done) |
| Encryption | Incremental backup engine (encrypt each child VHDX independently) |
| Cross-Sector Restore | Core restore engine (Phase 1, done) |
| Image Catalog | Incremental chains + sidecar metadata to catalog |
| Cloud Storage | Network destination infrastructure, resumable operations |
| CLI | All core services (backup, restore, verify, partition) |
| Recovery Media Builder | PhoenixPE compatibility (Phase 2), partition manager (Phase 2) |
| Deduplication | Stable backup format (don't change the storage layer until it's settled) |

---

## Implementation Order (Tentative)

| Order | Feature | Rationale |
|-------|---------|-----------|
| 1 | VM Integration | Already planned in detail, high user value |
| 2 | Encryption | High user demand, security-critical for cloud prep |
| 3 | CLI | Enables automation, pairs with scheduling from Phase 2 |
| 4 | Cross-Sector Restore | Removes a known limitation |
| 5 | Image Catalog | Better UX as backup count grows |
| 6 | Cloud Storage | Builds on encryption + network infra |
| 7 | Recovery Media Builder | Builds on Phase 2 PE compat |
| 8 | Deduplication | Evaluate ROI before committing |

---

## Success Criteria

- [ ] VHDX backups can be booted as VMs with WHPX acceleration
- [ ] Backups can be encrypted with AES-256, restored with password/key-file
- [ ] Restore works between 512B and 4K sector disks
- [ ] CLI can perform backup/restore/verify without the GUI
- [ ] At least one cloud provider works as a backup destination
- [ ] All features build and run on x64 and ARM64
- [ ] No new NuGet packages that lack ARM64 support
