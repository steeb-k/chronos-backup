# AGENTS.md - Development Guide for Chronos

## Project Overview

Chronos is a Windows disk imaging utility built with WinUI 3 and .NET 10. It supports backup, restore, clone, verify, and mount operations for disks and partitions with VSS support and Zstandard compression.

### Solution Structure

```
Chronos.sln
├── src/
│   ├── Chronos.App/        - WinUI 3 application (MVVM with CommunityToolkit.Mvvm)
│   ├── Chronos.Core/       - Imaging engines, compression, VSS, disk I/O
│   ├── Chronos.Native/     - P/Invoke wrappers for Win32 APIs
│   ├── Chronos.Common/     - Shared utilities and extensions
│   └── temp_winui/         - Temporary/experimental UI code
└── tests/
    ├── Chronos.Core.Tests/ - xUnit unit tests
    ├── Chronos.Integration.Tests/
    ├── AllocatedRangesTestHarness/
    └── VirtualDiskTestHarness/
```

---

## Build Commands

### Build the Solution

```powershell
# Build for x64 (default)
dotnet build Chronos.sln

# Build for specific platform
dotnet build Chronos.sln -p:Platform=x64
dotnet build Chronos.sln -p:Platform=ARM64
dotnet build Chronos.sln -p:Platform=x86
```

### Release Builds (Self-Contained)

```powershell
# x64 Release
dotnet publish src/Chronos.App.csproj -c Release -r win-x64 --self-contained -p:Platform=x64

# ARM64 Release
dotnet publish src/Chronos.App.csproj -c Release -r win-arm64 --self-contained -p:Platform=ARM64
```

### Run the Application

```powershell
# Run x64 debug build
dotnet run --project src/Chronos.App.csproj -p:Platform=x64

# Run ARM64 debug build
dotnet run --project src/Chronos.App.csproj -p:Platform=ARM64
```

### Create Release Distribution

```powershell
# Creates installers and portable ZIPs (requires Inno Setup 6+)
.\scripts\Build-Release.ps1 -Version "1.0.0"
```

---

## Test Commands

### Run All Tests

```powershell
dotnet test
```

### Run Single Test

```powershell
# Run a specific test method
dotnet test --filter "FullyQualifiedName~Chronos.Core.Tests.UnitTest1.Test1"

# Run tests in a specific project
dotnet test tests/Chronos.Core.Tests/
```

### Test Projects

| Project | Framework | Test Runner |
|---------|-----------|-------------|
| Chronos.Core.Tests | net10.0-windows10.0.19041.0 | xUnit |
| Chronos.Integration.Tests | net10.0-windows10.0.19041.0 | xUnit |

---

## Code Style Guidelines

### General Conventions

- **.NET 10** with `net10.0-windows10.0.19041.0` target framework
- **Nullable reference types** enabled (`<Nullable>enable</Nullable>`)
- **Implicit usings** enabled
- **Platforms**: x86, x64, ARM64 (AnyCPU by default)

### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Classes/Interfaces | PascalCase | `BackupEngine`, `IVssService` |
| Methods | PascalCase | `ExecuteAsync`, `GetDisksAsync` |
| Properties | PascalCase | `SelectedDisk`, `AvailableDisks` |
| Private fields | PascalCase (no underscore prefix) | `diskReader`, `compressionProvider` |
| Parameters | camelCase | `diskNumber`, `sourceHandle` |
| Constants | PascalCase | `CopyBufferSize` |
| Enums | PascalCase | `BackupType`, `PartitionInfo` |

### File Organization

- **One public class per file** (filename matches class name)
- **Namespace** matches folder structure: `Chronos.Core.Imaging`
- **Using statements** at top of file, sorted alphabetically
- **Global usings** in `src/Imports.cs` for WinUI types

### Imports Order

1. System namespaces (`System`, `System.IO`, etc.)
2. Third-party namespaces (`CommunityToolkit`, `Serilog`, `Moq`)
3. Project namespaces (`Chronos.Core.*`, `Chronos.App.*`)

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Chronos.Core.Disk;
using Chronos.Core.Services;
using Serilog;
```

### Code Style Rules

#### Properties and Fields

```csharp
// Public properties - use [ObservableProperty] with CommunityToolkit.Mvvm
[ObservableProperty]
public partial List<DiskInfo> AvailableDisks { get; set; } = new();

// Nullable reference types
[ObservableProperty]
public partial DiskInfo? SelectedDisk { get; set; }

// Private readonly fields for injected dependencies
private readonly IDiskReader _diskReader;
private readonly IDiskWriter _diskWriter;
```

#### Constructor Injection

```csharp
public BackupEngine(
    IDiskReader diskReader,
    IDiskWriter diskWriter,
    IVirtualDiskService virtualDiskService)
{
    _diskReader = diskReader ?? throw new ArgumentNullException(nameof(diskReader));
    _diskWriter = diskWriter ?? throw new ArgumentNullException(nameof(diskWriter));
    _virtualDiskService = virtualDiskService ?? throw new ArgumentNullException(nameof(virtualDiskService));
}
```

#### Null Checking

- Use `ArgumentNullException.ThrowIfNull()` for parameters (C# 10+)
- Use nullable reference types (`?`) for optional dependencies
- Use null-conditional operators (`?.`) and null-coalescing (`??`)

#### Error Handling

```csharp
try
{
    // Operation logic
}
catch (OperationCanceledException)
{
    // Handle cancellation - rethrow
    throw;
}
catch (Exception ex)
{
    // Log error with Win32 code if applicable
    int win32 = Marshal.GetLastWin32Error();
    Log.Error(ex, "Operation failed. Win32 error: {Win32}", win32);
    throw;
}
```

#### Async/Await

- Always use `.ConfigureAwait(false)` for library code
- Use `CancellationToken` for cancellable operations
- Use `IProgressReporter?` for progress reporting

```csharp
public async Task ExecuteAsync(
    BackupJob job,
    IProgressReporter? progressReporter = null,
    CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(job);
    
    await DoWorkAsync(job, cancellationToken).ConfigureAwait(false);
}
```

#### Documentation

- Add XML doc comments for public APIs
- Use `<summary>` tags for class and method descriptions

```csharp
/// <summary>
/// Implementation of backup engine with sector-by-sector copy and compression support.
/// </summary>
public class BackupEngine : IBackupEngine
{
    /// <summary>
    /// Saves a sidecar JSON file alongside the image with disk/partition metadata.
    /// </summary>
    private async Task SaveSidecarAsync(...)
}
```

### Logging

- Use **Serilog** for structured logging
- Log levels: `Log.Debug()`, `Log.Information()`, `Log.Warning()`, `Log.Error()`
- Include relevant context in log messages

```csharp
Log.Information("Backup started: Source={Source}, Dest={Dest}", job.SourcePath, job.DestinationPath);
Log.Error(ex, "Operation failed. Win32 error: {Win32}", win32);
```

### Testing Guidelines

- Use **xUnit** as test framework
- Use **Moq** for mocking interfaces
- Use `[Fact]` attribute for test methods

```csharp
using Xunit;
using Moq;

public class BackupEngineTests
{
    [Fact]
    public async Task ExecuteAsync_ValidJob_BackupCompletes()
    {
        // Arrange
        var mockReader = new Mock<IDiskReader>();
        // ...
        
        // Act
        var engine = new BackupEngine(mockReader.Object, ...);
        await engine.ExecuteAsync(job);
        
        // Assert
        mockReader.Verify(r => r.OpenDiskAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

---

## Key Technologies

| Component | Technology |
|-----------|------------|
| UI Framework | WinUI 3 |
| MVVM | CommunityToolkit.Mvvm |
| Logging | Serilog |
| Compression | Zstandard |
| Testing | xUnit, Moq |
| Virtual Disks | VHDX (Windows native) |
| Snapshots | VSS (Volume Shadow Copy) |

---

## Common Development Tasks

### Adding a New Service

1. Create interface in `Chronos.Core` (e.g., `ICompressionProvider`)
2. Create implementation (e.g., `ZstdCompressionProvider`)
3. Register in dependency injection if applicable
4. Add tests in `Chronos.Core.Tests`

### Adding a New ViewModel

1. Create in `Chronos.App/ViewModels/`
2. Inherit from `ObservableObject` or use `[ObservableObject]`
3. Use `[ObservableProperty]` for observable properties
4. Use `[RelayCommand]` for commands

### Working with Disk I/O

- All disk operations require **Administrator privileges**
- Use `DiskReader` and `DiskWriter` from `Chronos.Core.Disk`
- Handle sector sizes properly (512-byte and 4K-native drives)
- Use `CancellationToken` for interruptible operations

---

## WinPE Compatibility

Chronos is designed to run in Windows PE environments (e.g., WinPE, PhoenixPE) for deployment scenarios. The app auto-detects PE and adapts accordingly.

### Key Changes for PE Support

#### 1. PeEnvironment Detection (`Chronos.Common/Helpers/PeEnvironment.cs`)

```csharp
// Detection
bool isWinPE = PeEnvironment.IsWinPE;

// Capability probing
var caps = PeEnvironment.Capabilities;
bool hasWmi = caps.HasWmi;      // WMI may crash in some PE variants
bool hasVss = caps.HasVss;      // VSS not available in PE
bool hasDwm = caps.HasDwm;      // DWM required for WinUI 3
```

Detects PE via multiple heuristics: registry keys (`WinPE`, `MiniNT`), `startnet.cmd`, `winpeshl.exe`.

#### 2. Mica/Acrylic Backdrop Disabled in PE

In `MainWindow.xaml.cs`, backdrop effects are skipped when `PeEnvironment.IsWinPE` is true:

```csharp
if (!Chronos.Common.Helpers.PeEnvironment.IsWinPE)
{
    if (MicaController.IsSupported())
        this.SystemBackdrop = new MicaBackdrop();
    else if (DesktopAcrylicController.IsSupported())
        this.SystemBackdrop = new DesktopAcrylicBackdrop();
}
```

This avoids compositor crashes in PE where the DWM interfaces are incomplete.

#### 3. WMI Fallback for Disk Enumeration

`DiskEnumerator.cs` falls back to IOCTL-only enumeration when WMI is unavailable:

```csharp
if (!PeEnvironment.Capabilities.HasWmi)
{
    // Use IOCTL_DISK_GET_DRIVE_LAYOUT_EX instead of WMI
    return EnumerateDisksIoctlOnly();
}
```

#### 4. AppData Directory Fallback

PE environments may have empty `%LOCALAPPDATA%`. `PeEnvironment.GetAppDataDirectory()` provides fallbacks:

1. `%LOCALAppData%\Chronos` (standard)
2. `[exe]\AppData` (portable)
3. `X:\Chronos` (PE RAM drive)
4. `%TEMP%\Chronos` (last resort)

#### 5. Update Checks Skipped in PE

```csharp
if (!PeEnvironment.IsWinPE)
{
    _ = CheckForUpdatesOnStartupAsync();
}
```

#### 6. Self-Test Diagnostic Mode

Run `Chronos.exe --selftest` to probe PE capabilities without launching the UI:

```powershell
Chronos.exe --selftest
```

Outputs: `PeEnvironment.IsWinPE`, all `PeCapabilities` flags, disk enumeration status.

### PE-Specific Behaviors

| Feature | Full Windows | WinPE |
|---------|-------------|-------|
| Mica/Acrylic backdrop | Enabled | Disabled |
| WMI disk enumeration | Primary | Fallback to IOCTL |
| VSS (Volume Shadow Copy) | Available | Not available |
| Update checking | Enabled | Disabled |
| AppData location | %LOCALAPPDATA% | Portable fallback |

### Deployment to WinPE

See `README-WinPE.md` for detailed instructions. Key points:
- Copy ~25 system DLLs from a full Windows system (see `scripts/Extract-WimDeps.ps1`)
- Run `Test-WinPE-Readiness.ps1` before launching Chronos
