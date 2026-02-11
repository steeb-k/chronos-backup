# Phase 2 Completion Plan

Plan for implementing Phase 2 features to enhance Chronos with advanced backup capabilities, scheduling, and improved user experience.

---

## Current Status (Post Phase 1)

| Feature Category | Completed | Missing |
|------------------|-----------|---------|
| Basic Backup/Restore | ✅ Full disk/partition backup, VHDX, VSS, compression, clone | Incremental/differential |
| UI Implementation | ✅ ViewModels, core logic | Full XAML pages, wizards, history view |
| Advanced Features | ✅ Operation history backend | Scheduling, notifications, network support |
| Performance | ✅ Basic optimization | Multi-threading, deduplication |
| User Experience | ✅ Settings persistence | Backup catalog, chain management, wizards |

---

## 1. Incremental & Differential Backup

**Priority: High**

### 1.1 Backup Chain Infrastructure

- **File**: `Chronos.Core/Imaging/BackupChain.cs`
- Track backup relationships:
  - Base (full) backup
  - Incremental backups (changes since last backup)
  - Differential backups (changes since base)
- Metadata storage:
  - Chain ID and sequence numbers
  - Parent backup references
  - Timestamp and size information
  - Block-level change tracking

### 1.2 Change Detection

- **File**: `Chronos.Core/Imaging/ChangeDetector.cs`
- Implement change bitmap tracking
- Use NTFS USN Journal for file-level changes
- Compare disk sectors between backups
- Store changed block bitmaps in metadata

### 1.3 Incremental Backup Engine

- Extend `BackupEngine` with incremental mode
- Read previous backup metadata
- Copy only changed sectors/blocks
- Update change bitmap
- Link to parent backup in metadata

### 1.4 Differential Backup Engine

- Similar to incremental but always reference base backup
- Simpler restore process (base + differential only)
- Larger than incremental but faster restore

### 1.5 Chain Management Service

- **File**: `Chronos.App/Services/BackupChainService.cs`
- List all backups in a chain
- Validate chain integrity
- Display chain tree structure
- Delete old backups with chain maintenance
- Merge incremental backups periodically

---

## 2. UI Pages & Wizards

**Priority: High**

### 2.1 Complete XAML Pages

- **BackupPage.xaml**: Full implementation with clone mode toggle
- **RestorePage.xaml**: Complete restore workflow
- **BrowsePage.xaml**: Mount/dismount UI, file extraction
- **VerifyPage.xaml**: Image verification interface
- **HistoryPage.xaml**: Operation history with filtering
- **OptionsPage.xaml**: All settings with validation

### 2.2 Backup Wizard

- **File**: `Chronos.App/Views/Wizards/BackupWizard.xaml`
- Multi-step wizard:
  1. Select backup type (full/incremental/differential/clone)
  2. Choose source disk/partition
  3. Select destination (file or disk for clone)
  4. Configure options (compression, VSS, verification)
  5. Review and confirm
  6. Progress with real-time updates
- Save wizard preferences

### 2.3 Restore Wizard

- **File**: `Chronos.App/Views/Wizards/RestoreWizard.xaml`
- Multi-step wizard:
  1. Select image (with chain visualization if applicable)
  2. Choose target disk/partition
  3. Safety warnings and confirmations
  4. Review restore plan
  5. Progress with verification option

### 2.4 Image Catalog View

- **File**: `Chronos.App/Views/CatalogPage.xaml`
- Visual catalog of all backups
- Group by backup chains
- Show disk space usage
- Quick actions: mount, verify, delete, restore
- Search and filter capabilities

---

## 3. Backup Scheduling

**Priority: Medium**

### 3.1 Scheduler Service

- **File**: `Chronos.App/Services/SchedulerService.cs`
- Create scheduled tasks using Windows Task Scheduler
- Support multiple schedules per backup job
- Schedule types:
  - One-time
  - Daily (with time)
  - Weekly (select days)
  - Monthly
  - On event (system startup, user logon)

### 3.2 Background Service

- **File**: `Chronos.App/Services/BackgroundTaskService.cs`
- Run scheduled backups when app is closed
- Use Windows Service or Scheduled Task
- Pre-check conditions before starting
- Generate execution logs

### 3.3 Schedule Management UI

- **File**: `Chronos.App/Views/SchedulePage.xaml`
- List all scheduled backups
- Add/edit/delete schedules
- Enable/disable schedules
- View next run time
- View schedule execution history

### 3.4 Missed Backup Handling

- Detect missed scheduled backups
- Option to run immediately on app launch
- Adjust schedule if system was off

---

## 4. Notifications

**Priority: Medium**

### 4.1 Windows Notifications

- **File**: `Chronos.App/Services/NotificationService.cs`
- Use Windows Toast Notifications
- Notify on:
  - Backup completion (success/failure)
  - Scheduled backup start
  - Low disk space warnings
  - Verification failures
- Actionable notifications (view logs, dismiss)

### 4.2 Email Notifications

- **File**: `Chronos.App/Services/EmailNotificationService.cs`
- SMTP configuration in settings
- Email templates for different events
- Attach summary reports
- Support for:
  - Direct SMTP
  - Gmail/Outlook OAuth
  - SendGrid API (optional)

### 4.3 Notification Settings

- Configure notification preferences per event type
- Email recipient list
- Test notification button
- Quiet hours configuration

---

## 5. Network Destination Support

**Priority: Medium**

### 5.1 Network Path Handling

- **File**: `Chronos.Core/Storage/NetworkStorageProvider.cs`
- Support UNC paths (`\\server\share\backups`)
- Credential management for network shares
- Connection validation before backup
- Automatic reconnection on failure

### 5.2 Credential Store

- **File**: `Chronos.App/Services/CredentialStore.cs`
- Secure storage using Windows Credential Manager
- Store network credentials per destination
- Support for different authentication methods

### 5.3 Network Destination UI

- Browse network shares
- Test connection button
- Save credentials option
- Show available space on network drive

---

## 6. Performance Enhancements

**Priority: Medium**

### 6.1 Multi-threaded Compression

- **File**: `Chronos.Core/Compression/ParallelCompressionProvider.cs`
- Parallel zstd compression streams
- Chunk-based compression for large files
- Configurable thread count
- Maintain compatibility with single-threaded decompression

### 6.2 I/O Optimization

- Increase buffer sizes for sequential operations
- Use memory-mapped files for metadata
- Implement read-ahead buffering
- Optimize sector alignment

### 6.3 Async Everywhere

- Ensure all long-running operations are fully async
- Proper cancellation token propagation
- Background worker pools for CPU-intensive tasks

---

## 7. Deduplication (Stretch Goal)

**Priority: Low**

### 7.1 Block-Level Deduplication

- **File**: `Chronos.Core/Deduplication/DeduplicationEngine.cs`
- Content-addressed storage
- SHA-256 hash per block
- Shared block store across backups
- Reference counting for block deletion

### 7.2 Dedup Metadata

- Block hash index
- Block reference map
- Space savings statistics

### 7.3 Dedup in Backup Flow

- Check existing blocks before writing
- Store only unique blocks
- Update reference counts
- Background garbage collection

---

## 8. Image Management & Catalog

**Priority: High**

### 8.1 Image Catalog Database

- **File**: `Chronos.App/Services/ImageCatalogService.cs`
- SQLite database for image metadata
- Track all backups and their locations
- Store backup chains and relationships
- Image properties:
  - Source disk info
  - Creation date/time
  - Size (compressed/uncompressed)
  - Backup type
  - Status (verified, mountable, etc.)

### 8.2 Auto-Discovery

- Scan configured directories for existing images
- Import image metadata
- Detect orphaned images
- Rebuild catalog from image metadata files

### 8.3 Smart Cleanup

- Retention policies:
  - Keep last N full backups
  - Keep incremental/differential for X days
  - Keep one backup per week/month
- Space reclamation calculations
- Safe deletion with chain validation

---

## 9. Enhanced Error Handling & Recovery

**Priority: Medium**

### 9.1 Resume Failed Operations

- **File**: `Chronos.Core/Imaging/ResumableBackup.cs`
- Save operation state periodically
- Detect incomplete operations on startup
- Resume from last checkpoint
- Sector-level checkpoint tracking

### 9.2 Better Error Messages

- Context-specific error messages
- Suggested solutions for common errors
- Link to documentation/troubleshooting
- Error reporting mechanism

### 9.3 Pre-flight Checks

- Comprehensive validation before operations
- Disk space checks
- Permission verification
- Hardware compatibility checks
- Network connectivity (for network destinations)

---

## 10. Quality of Life Improvements

**Priority: Medium**

### 10.1 Drag & Drop

- Drag VHDX files to restore/verify/mount
- Drag disk icons for backup source selection

### 10.2 Quick Actions

- System tray icon with quick backup option
- Recent backups menu
- Quick mount last image
- One-click scheduled backup

### 10.3 Comparison Tools

- Compare two backups
- Show changed files between backups
- Size difference reports

### 10.4 Export/Import Settings

- Export all settings to file
- Import settings from file
- Backup configuration templates

---

## Suggested Implementation Order

1. **UI Pages & Wizards** (Week 1-2)
   - Complete all XAML pages
   - Implement backup/restore wizards
   - Create image catalog view
   - _Rationale_: Makes Phase 1 features fully usable

2. **Image Catalog & Management** (Week 2-3)
   - Implement catalog database
   - Auto-discovery and import
   - Smart cleanup and retention
   - _Rationale_: Foundation for backup chains

3. **Incremental/Differential Backup** (Week 3-5)
   - Change detection infrastructure
   - Backup chain tracking
   - Incremental backup engine
   - Differential backup engine
   - _Rationale_: Core Phase 2 feature

4. **Backup Scheduling** (Week 5-6)
   - Scheduler service
   - Schedule management UI
   - Background task integration
   - _Rationale_: High user value

5. **Notifications** (Week 6-7)
   - Windows toast notifications
   - Email notification service
   - Notification settings
   - _Rationale_: Complements scheduling

6. **Network Support** (Week 7-8)
   - Network path handling
   - Credential management
   - Network destination UI
   - _Rationale_: Enables enterprise scenarios

7. **Performance Enhancements** (Week 8-9)
   - Multi-threaded compression
   - I/O optimization
   - Profiling and tuning
   - _Rationale_: Improve user experience

8. **Error Handling & Recovery** (Week 9-10)
   - Resumable operations
   - Better error messages
   - Pre-flight checks
   - _Rationale_: Robustness and reliability

9. **Quality of Life** (Week 10-11)
   - Drag & drop
   - Quick actions
   - Comparison tools
   - Export/import settings
   - _Rationale_: Polish and usability

10. **Deduplication** (Week 11-12, Optional)
    - Block-level deduplication
    - Dedup metadata
    - Integration with backup flow
    - _Rationale_: Advanced feature, time permitting

---

## Files to Create or Modify

### New Core Files
- `Chronos.Core/Imaging/BackupChain.cs`
- `Chronos.Core/Imaging/ChangeDetector.cs`
- `Chronos.Core/Imaging/IncrementalBackupEngine.cs`
- `Chronos.Core/Imaging/ResumableBackup.cs`
- `Chronos.Core/Storage/NetworkStorageProvider.cs`
- `Chronos.Core/Compression/ParallelCompressionProvider.cs`
- `Chronos.Core/Deduplication/DeduplicationEngine.cs`

### New Service Files
- `Chronos.App/Services/BackupChainService.cs`
- `Chronos.App/Services/ImageCatalogService.cs`
- `Chronos.App/Services/SchedulerService.cs`
- `Chronos.App/Services/BackgroundTaskService.cs`
- `Chronos.App/Services/NotificationService.cs`
- `Chronos.App/Services/EmailNotificationService.cs`
- `Chronos.App/Services/CredentialStore.cs`

### New View Files
- `Chronos.App/Views/CatalogPage.xaml/.cs`
- `Chronos.App/Views/SchedulePage.xaml/.cs`
- `Chronos.App/Views/HistoryPage.xaml/.cs`
- `Chronos.App/Views/Wizards/BackupWizard.xaml/.cs`
- `Chronos.App/Views/Wizards/RestoreWizard.xaml/.cs`

### Complete Existing Pages
- `Chronos.App/Views/BackupPage.xaml` (full implementation)
- `Chronos.App/Views/RestorePage.xaml` (full implementation)
- `Chronos.App/Views/BrowsePage.xaml` (full implementation)
- `Chronos.App/Views/VerifyPage.xaml` (full implementation)
- `Chronos.App/Views/OptionsPage.xaml` (full implementation)

### Modified Core Files
- `Chronos.Core/Imaging/BackupEngine.cs` (add incremental/differential support)
- `Chronos.Core/Models/BackupJob.cs` (add scheduling fields)
- `Chronos.App/ViewModels/*.cs` (complete implementations)

---

## Architecture Considerations

### Database Choice
- **SQLite** for image catalog and metadata
  - Lightweight, serverless
  - No external dependencies
  - Cross-platform compatible
  - Works on all architectures (x86/x64/ARM64)
  - Package: `Microsoft.Data.Sqlite`

### Scheduling
- **Windows Task Scheduler** via COM interop
  - Native Windows integration
  - Survives system reboots
  - User can manage tasks in Task Scheduler UI
  - Works when app is closed

### Network Storage
- Standard .NET file I/O with UNC paths
- Windows Credential Manager for credential storage
- Test connectivity before operations
- Graceful fallback to local storage on network failure

### Performance
- CPU core detection for thread pool sizing
- Memory-aware buffering (scale based on available RAM)
- Profile before optimizing
- Configurable performance vs. compatibility modes

---

## Success Criteria for Phase 2

✅ Incremental and differential backups working reliably  
✅ Backup chains properly tracked and manageable  
✅ Scheduling integrated with Windows Task Scheduler  
✅ Email and toast notifications functional  
✅ Network destinations (UNC paths) fully supported  
✅ Multi-threaded compression showing measurable speedup  
✅ All XAML pages fully implemented and polished  
✅ Image catalog with auto-discovery and smart cleanup  
✅ Wizards guide users through complex operations  
✅ Resumable operations after interruption  
✅ <500ms UI response time for all interactions  
✅ Comprehensive error messages with suggested actions  
✅ Full ARM64 compatibility maintained  

---

## Testing Requirements

### Functional Testing
- Test backup chains with multiple incremental/differential backups
- Verify restore from any point in chain
- Test scheduler creates tasks correctly
- Verify notifications trigger on all events
- Test network paths with various authentication methods
- Validate multi-threaded compression produces identical output

### Integration Testing
- Test backup → verify → restore → compare workflow
- Test scheduling → execution → notification chain
- Test network failure scenarios and recovery

### Performance Testing
- Benchmark multi-threaded vs. single-threaded compression
- Measure deduplication space savings
- Profile memory usage with large backup chains
- Test with various disk speeds (HDD, SATA SSD, NVMe)

### UI Testing
- Wizard flow completion without errors
- Catalog responsiveness with 1000+ images
- Schedule management with complex schedules
- Error message clarity and actionability

---

## Risk Mitigation

### Backup Chain Complexity
- **Risk**: Chain corruption could lose multiple backups
- **Mitigation**: 
  - Validate chain integrity before operations
  - Keep periodic full backups
  - Implement chain repair tools

### Scheduling Reliability
- **Risk**: Missed backups if system is off
- **Mitigation**:
  - Log missed schedules
  - Offer catch-up on next system start
  - Email alerts for missed backups

### Network Connectivity
- **Risk**: Network failures mid-backup
- **Mitigation**:
  - Implement retry logic
  - Resume support for network operations
  - Fallback to local cache then sync

### Performance Degradation
- **Risk**: Multi-threading overhead on low-end systems
- **Mitigation**:
  - Auto-detect optimal thread count
  - Allow manual override
  - Provide performance/compatibility mode toggle

---

## Future Considerations (Phase 3)

After Phase 2 completion, consider:
- Bootable recovery media creation
- WinPE integration for bare-metal restore
- Cloud storage integration (Azure, AWS, OneDrive)
- AES-256 encryption
- Split archives for DVD/USB distribution
- Command-line interface for automation
- API for third-party integration

---

## Cross-Sector-Size Restore Support

**Status**: Blocked with clear error message (Phase 1 implementation)  
**Target**: Phase 2 or 3

### Background
Restoring images between disks with different sector sizes (e.g., 512B HDD → 4K UFS) requires GPT partition table translation because GPT uses LBA (Logical Block Address) which is sector-relative.

### Current Implementation (Phase 1)
- Source sector size stored in sidecar metadata during backup
- Restore validates sector size match before proceeding
- Clear error message explains incompatibility

### Future Implementation (GPT Translation)
1. **GPT Parser** (`Chronos.Core/Disk/GptParser.cs`)
   - Parse protective MBR at LBA 0
   - Parse GPT header at LBA 1
   - Parse partition entries (LBA 2-33)
   - Validate CRC32 checksums

2. **GPT Writer with LBA Translation**
   - Recalculate LBA values: `new_lba = (old_lba * old_sector_size) / new_sector_size`
   - Verify partition alignment to new sector boundaries
   - Regenerate CRC32 checksums
   - Write primary and backup GPT

3. **Alignment Validation**
   - Check all partitions align to target sector boundaries
   - Fail gracefully with clear message if alignment impossible

4. **NTFS Boot Sector Update** (optional)
   - Parse NTFS boot sector `BytesPerSector` field
   - Update if sector size changed

### Complexity
- 4K → 512B: Easier (each 4K sector maps to 8 × 512B)
- 512B → 4K: Harder (must verify all partitions are 4K-aligned)
