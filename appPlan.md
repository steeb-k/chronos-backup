# Chronos
## Modern Disk Imaging Utility for Windows
**x86/AMD64/ARM64 Support | GPLv3 Licensed**

---

## Project Overview

Chronos is a modern, open-source disk imaging utility for Windows with a beautiful WinUI3 interface. It provides simple, performant disk and partition backup, restore, verification, and browsing capabilities with VSS integration for live system backups.

### Core Principles
- **Simplicity First**: Clean, intuitive interface with minimal learning curve
- **Modern Design**: WinUI3 with Acrylic/Mica effects
- **Performance**: Fast compression and efficient I/O operations
- **Cross-Architecture**: Full support for x86, AMD64, and ARM64 Windows systems
- **Open Source**: GPLv3 licensed for transparency and community contribution

---

## Technical Stack

### Framework & UI
- **.NET 10**: Modern .NET for performance and long-term support
- **WinUI3**: Native Windows UI with modern Fluent Design
- **Windows App SDK**: Latest Windows platform features
- **XAML**: Declarative UI design

### Core Technologies
- **VSS (Volume Shadow Copy Service)**: Consistent backups of running systems
- **VHD/VHDX Format**: Native Windows virtual disk format for image storage
- **Zstandard (zstd)**: Fast compression with excellent ratios
- **P/Invoke**: Direct Windows API access for disk operations
- **NTFS/ReFS**: Full filesystem support

### Architecture Pattern
- **MVVM (Model-View-ViewModel)**: Clean separation of concerns
- **Dependency Injection**: Microsoft.Extensions.DependencyInjection
- **Async/Await**: Non-blocking operations throughout
- **Event-driven**: Progress reporting and cancellation support

---

## Interface Design

### Navigation Structure
```
 ______________________________________________
|           |                            _ o x |
| LOGO IMG  |                                  |
|           |   _________________    
| CHRONOS   |  |Disk Backup      |
|img/bkp sw |   _________________
|           |  |Partition Backup |
|> Backup   |   _________________
|  Restore  |  |Disk Clone       |
|  Verify   |   _________________
|  Browse   |  |Partition Clone  |
|           |
|           |
|           |
|           |
|           |
|           |
|  Options  |
 ______________________________________________
```

### Pages
1. **Backup**: Disk backup, Partition backup, Disk clone, Partition clone
2. **Restore**: Select image and target for restoration
3. **Verify**: Image integrity checking and hash verification
4. **Browse**: Mount images to drive letter or folder, browse contents
5. **Options**: Application settings, compression level, default paths

---

## Feature Set

### Phase 1: Core Functionality (Initial Release)
✅ **Backup Operations**
- Full disk backup to VHDX
- Full partition backup to VHDX
- Disk-to-disk clone
- Partition-to-partition clone
- VSS integration for live system backups
- Zstandard compression with configurable levels
- Progress reporting with speed/ETA
- Pause/Resume/Cancel support

✅ **Restore Operations**
- Restore VHDX to disk
- Restore VHDX to partition
- Sector-by-sector verification during restore
- Pre-restore safety checks

✅ **Verification**
- Image integrity verification
- Hash validation (SHA-256)
- Filesystem consistency checks

✅ **Browse/Mount**
- Mount VHDX to drive letter
- Mount VHDX to folder (virtual folder)
- Automatic dismount on app exit
- Read-only mounting by default
- File extraction from images

✅ **UI/UX**
- WinUI3 with Mica/Acrylic effects
- Responsive design
- Dark/Light theme support
- Operation history log
- Settings persistence

### Phase 2: Enhanced Features (Future)
🔲 Incremental backup support
🔲 Differential backup support
🔲 Backup scheduling
🔲 Email notifications
🔲 Network destination support
🔲 Multi-threaded compression
🔲 Deduplication

### Phase 3: Advanced Features (Long-term)
🔲 Bootable recovery media creation (USB/ISO)
🔲 WinPE integration for bare-metal restore
🔲 Cloud storage support
🔲 Encryption (AES-256)
🔲 Split archives for large images
🔲 Command-line interface

---

## Project Structure

```
Chronos/
│
├── src/
│   ├── Chronos.App/                    # WinUI3 Application
│   │   ├── Views/                      # XAML pages
│   │   │   ├── MainWindow.xaml
│   │   │   ├── BackupPage.xaml
│   │   │   ├── RestorePage.xaml
│   │   │   ├── VerifyPage.xaml
│   │   │   ├── BrowsePage.xaml
│   │   │   └── OptionsPage.xaml
│   │   ├── ViewModels/                 # View models
│   │   │   ├── MainViewModel.cs
│   │   │   ├── BackupViewModel.cs
│   │   │   ├── RestoreViewModel.cs
│   │   │   ├── VerifyViewModel.cs
│   │   │   ├── BrowseViewModel.cs
│   │   │   └── OptionsViewModel.cs
│   │   ├── Controls/                   # Custom controls
│   │   ├── Converters/                 # XAML converters
│   │   ├── Services/                   # UI services
│   │   ├── Assets/                     # Images, icons
│   │   ├── App.xaml
│   │   └── App.xaml.cs
│   │
│   ├── Chronos.Core/                   # Core business logic
│   │   ├── Imaging/
│   │   │   ├── IBackupEngine.cs
│   │   │   ├── BackupEngine.cs
│   │   │   ├── RestoreEngine.cs
│   │   │   ├── CloneEngine.cs
│   │   │   └── VerificationEngine.cs
│   │   ├── Compression/
│   │   │   ├── ICompressionProvider.cs
│   │   │   └── ZstdCompressionProvider.cs
│   │   ├── VirtualDisk/
│   │   │   ├── IVirtualDiskService.cs
│   │   │   ├── VhdxService.cs
│   │   │   └── MountService.cs
│   │   ├── VSS/
│   │   │   ├── IVssService.cs
│   │   │   ├── VssService.cs
│   │   │   └── VssSnapshot.cs
│   │   ├── Disk/
│   │   │   ├── DiskInfo.cs
│   │   │   ├── PartitionInfo.cs
│   │   │   └── DiskEnumerator.cs
│   │   ├── Progress/
│   │   │   ├── IProgressReporter.cs
│   │   │   └── OperationProgress.cs
│   │   └── Models/
│   │       ├── BackupJob.cs
│   │       ├── RestoreJob.cs
│   │       └── ImageMetadata.cs
│   │
│   ├── Chronos.Native/                 # P/Invoke & Native APIs
│   │   ├── Win32/
│   │   │   ├── DiskApi.cs
│   │   │   ├── VolumeApi.cs
│   │   │   └── VirtualDiskApi.cs
│   │   ├── VirtDisk/
│   │   │   └── VirtualDiskInterop.cs
│   │   └── Structures/
│   │       └── NativeStructures.cs
│   │
│   └── Chronos.Common/                 # Shared utilities
│       ├── Extensions/
│       ├── Helpers/
│       └── Constants/
│
├── tests/
│   ├── Chronos.Core.Tests/
│   ├── Chronos.Integration.Tests/
│   └── Chronos.UI.Tests/
│
├── docs/
│   ├── ARCHITECTURE.md
│   ├── API.md
│   ├── USER_GUIDE.md
│   └── CONTRIBUTING.md
│
├── .github/
│   └── workflows/
│       ├── build.yml
│       └── release.yml
│
├── LICENSE (GPLv3)
├── README.md
├── CHANGELOG.md
└── Chronos.sln
```

---

## Implementation Roadmap

### Milestone 1: Project Setup & Infrastructure (Week 1)
- [x] Create solution structure
- [x] Set up WinUI3 project with .NET 10
- [x] Configure dependency injection
- [x] Implement MVVM infrastructure
- [x] Design application shell with navigation
- [x] Set up unit test projects
- [ ] Configure CI/CD pipeline

### Milestone 2: Native Layer & Disk Operations (Week 2-3)
- [ ] Implement P/Invoke wrappers for disk APIs
- [ ] Create DiskEnumerator for disk/partition discovery
- [ ] Build VHD/VHDX creation and manipulation
- [ ] Implement VSS snapshot creation/deletion
- [ ] Create disk reading/writing infrastructure
- [ ] Add proper error handling and logging

### Milestone 3: Compression & I/O (Week 3-4)
- [ ] Integrate Zstandard compression library
- [ ] Implement compression provider interface
- [ ] Create buffered I/O pipeline
- [ ] Add progress reporting infrastructure
- [ ] Implement cancellation token support
- [ ] Performance profiling and optimization

### Milestone 4: Backup Engine (Week 4-5)
- [ ] Implement full disk backup
- [ ] Implement partition backup
- [ ] Add VSS integration for live backups
- [ ] Create image metadata system
- [ ] Add backup verification
- [ ] Implement pause/resume functionality

### Milestone 5: Clone Engine (Week 5-6)
- [ ] Implement disk-to-disk clone
- [ ] Implement partition-to-partition clone
- [ ] Add sector-by-sector copying
- [ ] Implement progress tracking
- [ ] Add pre-clone validation

### Milestone 6: Restore & Verify (Week 6-7)
- [ ] Implement restore from VHDX
- [ ] Add pre-restore safety checks
- [ ] Create verification engine
- [ ] Implement hash validation
- [ ] Add filesystem integrity checks

### Milestone 7: Mount & Browse (Week 7-8)
- [ ] Implement VHDX mounting to drive letter
- [ ] Add folder mounting support
- [ ] Create browse interface
- [ ] Implement file extraction
- [ ] Add automatic dismount handling

### Milestone 8: UI Implementation (Week 8-10)
- [ ] Create all XAML pages
- [ ] Implement ViewModels for all pages
- [ ] Add operation wizards
- [ ] Create progress dialogs
- [ ] Implement settings page
- [ ] Add operation history log
- [ ] Polish UI/UX with animations

### Milestone 9: Testing & Polish (Week 10-11)
- [ ] Comprehensive unit testing
- [ ] Integration testing
- [ ] UI automation testing
- [ ] Performance testing
- [ ] Bug fixes and refinements
- [ ] Documentation completion

### Milestone 10: Release Preparation (Week 11-12)
- [ ] Create installer (MSIX package)
- [ ] Finalize documentation
- [ ] Create user guide
- [ ] Set up GitHub repository
- [ ] Prepare release notes
- [ ] Beta testing
- [ ] Version 1.0 Release

---

## Technical Considerations

### Performance Targets
- **Backup Speed**: >200 MB/s on modern SSDs
- **Compression Ratio**: 40-60% size reduction (typical data)
- **Memory Usage**: <500 MB for active operations
- **UI Responsiveness**: All operations async, <100ms UI response

### Security
- **Administrator Privileges**: Required for disk-level operations
- **UAC Integration**: Proper elevation requests
- **Safe Operations**: Multiple confirmation dialogs for destructive operations
- **Secure Cleanup**: Proper VSS snapshot cleanup

### Compatibility
- **Windows 10**: Version 1809+ (all architectures)
- **Windows 11**: Full support
- **ARM64**: Native ARM64 compilation
- **NTFS/ReFS**: Both filesystem types supported

### Error Handling
- Comprehensive exception handling
- User-friendly error messages
- Detailed logging for troubleshooting
- Automatic cleanup on failures

---

## Dependencies

### NuGet Packages
- `Microsoft.WindowsAppSDK` - WinUI3 framework
- `CommunityToolkit.Mvvm` - MVVM helpers
- `Microsoft.Extensions.DependencyInjection` - DI container
- `ZstdSharp` or `ZstdNet` - Zstandard compression
- `Serilog` - Structured logging
- `xunit` - Unit testing
- `Moq` - Mocking framework

### Windows Components
- Volume Shadow Copy Service (VSS)
- Virtual Disk Service (VirtDisk.dll)
- Windows Imaging API

---

## Success Criteria

### v1.0 Release
✅ Successfully backup and restore Windows system partition
✅ Support all Windows architectures (x86/x64/ARM64)
✅ VSS integration working reliably
✅ Compression achieving >40% space savings
✅ Mount/browse images without errors
✅ Clean, intuitive WinUI3 interface
✅ Comprehensive error handling
✅ Full documentation and user guide

---

## Future Roadmap

### v1.1 - Enhanced Backup
- Incremental/differential backups
- Backup scheduling

### v1.2 - Enterprise Features
- Network storage support
- Email notifications
- Multi-threaded operations

### v2.0 - Recovery Environment
- Bootable recovery media
- WinPE integration
- Bare-metal restore

### v2.1 - Cloud & Security
- Cloud storage integration
- AES-256 encryption
- Backup deduplication