# VM Integration Plan: QEMU with WHPX Acceleration

This document outlines the concrete steps to integrate QEMU-based VM functionality into Chronos, allowing users to boot VHDX backups as virtual machines for verification and file recovery.

---

## Overview

**Goal:** Allow users to boot any VHDX backup as a VM directly from Chronos, with hardware acceleration when available.

**Stack:**
- **QEMU** (GPLv2) - Virtual machine emulator/hypervisor
- **WHPX** (Windows Hypervisor Platform) - Hardware acceleration on Windows 10/11
- **OVMF** (BSD) - UEFI firmware for GPT disk booting

**Performance tiers:**
1. WHPX enabled → Near-native performance
2. WHPX unavailable → Software emulation (slower but functional)

---

## Phase 1: QEMU Infrastructure (Week 1)

### 1.1 Create QEMU Service

**File:** `src/Chronos.Core/VirtualMachine/IQemuService.cs`

```csharp
public interface IQemuService
{
    /// <summary>Check if QEMU binaries are available locally.</summary>
    bool IsQemuInstalled();
    
    /// <summary>Download QEMU binaries to app data folder.</summary>
    Task DownloadQemuAsync(IProgress<double>? progress, CancellationToken ct);
    
    /// <summary>Check if WHPX acceleration is available.</summary>
    bool IsWhpxAvailable();
    
    /// <summary>Launch a VHDX as a bootable VM.</summary>
    Task<QemuProcess> LaunchVmAsync(VmLaunchOptions options, CancellationToken ct);
    
    /// <summary>Get the path to QEMU binaries.</summary>
    string GetQemuPath();
}
```

**File:** `src/Chronos.Core/VirtualMachine/QemuService.cs`

Key implementation details:
- Store QEMU in `%LocalAppData%\Chronos\qemu\`
- Download from official QEMU Windows builds or self-hosted mirror
- Detect WHPX via `Hyper-V Hypervisor` service or WMI query

### 1.2 WHPX Detection

```csharp
public bool IsWhpxAvailable()
{
    // Method 1: Check for Hyper-V Hypervisor service
    try
    {
        using var sc = new ServiceController("vmms");
        return sc.Status == ServiceControllerStatus.Running;
    }
    catch { }
    
    // Method 2: Try to load WHPX and check capability
    // qemu-system-x86_64 -accel whpx -accel help
    return false;
}
```

### 1.3 QEMU Binary Management

**Download source options:**
1. **QEMU Official:** https://qemu.weilnetz.de/w64/ (Stefan Weil builds)
2. **Self-hosted:** Mirror on GitHub Releases for reliability
3. **Winget integration:** Detect if user has QEMU installed system-wide

**Required files (~80MB compressed, ~200MB extracted):**
```
qemu/
├── qemu-system-x86_64.exe
├── qemu-img.exe
├── share/
│   └── qemu/
│       ├── edk2-x86_64-code.fd    (OVMF UEFI firmware)
│       ├── edk2-i386-vars.fd
│       └── keymaps/
└── *.dll (dependencies)
```

---

## Phase 2: VM Launch Logic (Week 1-2)

### 2.1 VM Launch Options

**File:** `src/Chronos.Core/VirtualMachine/VmLaunchOptions.cs`

```csharp
public class VmLaunchOptions
{
    public required string VhdxPath { get; set; }
    public int MemoryMB { get; set; } = 4096;
    public int CpuCores { get; set; } = 2;
    public bool EnableNetwork { get; set; } = true;
    public bool ReadOnly { get; set; } = true;  // Protect original backup
    public bool UseUefi { get; set; } = true;   // Required for GPT disks
    public VmDisplayMode DisplayMode { get; set; } = VmDisplayMode.Sdl;
}

public enum VmDisplayMode
{
    Sdl,      // Default windowed display
    Gtk,      // Alternative display
    None,     // Headless (for automation)
    Vnc       // Remote access
}
```

### 2.2 QEMU Command Builder

**File:** `src/Chronos.Core/VirtualMachine/QemuCommandBuilder.cs`

```csharp
public class QemuCommandBuilder
{
    public string BuildCommand(VmLaunchOptions options, string qemuPath, bool useWhpx)
    {
        var args = new List<string>();
        
        // Acceleration
        if (useWhpx)
            args.Add("-accel whpx");
        else
            args.Add("-accel tcg");
        
        // CPU and memory
        args.Add($"-m {options.MemoryMB}");
        args.Add($"-smp {options.CpuCores}");
        
        // UEFI firmware for GPT disks
        if (options.UseUefi)
        {
            var ovmfPath = Path.Combine(qemuPath, "share", "qemu", "edk2-x86_64-code.fd");
            args.Add($"-bios \"{ovmfPath}\"");
        }
        
        // Disk - read-only snapshot mode to protect original
        var snapshotFlag = options.ReadOnly ? ",snapshot=on" : "";
        args.Add($"-drive file=\"{options.VhdxPath}\",format=vhdx,if=virtio{snapshotFlag}");
        
        // Display
        args.Add(options.DisplayMode switch
        {
            VmDisplayMode.Sdl => "-display sdl",
            VmDisplayMode.Gtk => "-display gtk",
            VmDisplayMode.None => "-display none",
            VmDisplayMode.Vnc => "-display vnc=:0",
            _ => "-display sdl"
        });
        
        // Network (user-mode NAT)
        if (options.EnableNetwork)
            args.Add("-nic user,model=virtio-net-pci");
        else
            args.Add("-nic none");
        
        // USB tablet for better mouse integration
        args.Add("-usb -device usb-tablet");
        
        return string.Join(" ", args);
    }
}
```

### 2.3 Process Management

**File:** `src/Chronos.Core/VirtualMachine/QemuProcess.cs`

```csharp
public class QemuProcess : IDisposable
{
    private Process? _process;
    
    public bool IsRunning => _process?.HasExited == false;
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;
    
    public event EventHandler? Exited;
    
    public void Start(string qemuExePath, string arguments)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = qemuExePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = false  // Show QEMU window
            },
            EnableRaisingEvents = true
        };
        _process.Exited += (s, e) => Exited?.Invoke(this, EventArgs.Empty);
        _process.Start();
    }
    
    public void Stop()
    {
        if (_process?.HasExited == false)
        {
            // Send ACPI shutdown signal via QEMU monitor (graceful)
            // Fallback to kill after timeout
            _process.Kill();
        }
    }
    
    public void Dispose() => _process?.Dispose();
}
```

---

## Phase 3: UI Integration (Week 2)

### 3.1 VM Launch Button on Browse Page

Add to `BrowsePage.xaml` alongside existing Mount/Unmount:

```xaml
<Button Content="Boot as VM" 
        Command="{Binding LaunchVmCommand}"
        IsEnabled="{Binding CanLaunchVm}" />
```

### 3.2 VM Settings Dialog

**File:** `src/Views/Dialogs/VmSettingsDialog.xaml`

Simple dialog allowing user to configure:
- Memory (slider: 2GB - 16GB, auto-detect sensible default)
- CPU cores (slider: 1-8, default 2)
- Read-only mode (checkbox, default on)
- Network enabled (checkbox, default on)

### 3.3 QEMU Download Progress

First-time VM launch should:
1. Check if QEMU installed
2. If not, show download dialog with progress bar
3. Download and extract (~80MB)
4. Verify integrity (SHA256)
5. Launch VM

### 3.4 ViewModel Changes

**File:** `src/Chronos.App/ViewModels/BrowseViewModel.cs`

Add:
```csharp
[ObservableProperty] public partial bool IsQemuAvailable { get; set; }
[ObservableProperty] public partial bool IsVmRunning { get; set; }

public bool CanLaunchVm => SelectedImage is not null && !IsVmRunning;

[RelayCommand]
private async Task LaunchVmAsync()
{
    if (!_qemuService.IsQemuInstalled())
    {
        // Show download dialog
        var dialog = new QemuDownloadDialog();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        
        await _qemuService.DownloadQemuAsync(dialog.Progress, CancellationToken.None);
    }
    
    var options = new VmLaunchOptions
    {
        VhdxPath = SelectedImage!.Path,
        ReadOnly = true
    };
    
    // Show settings dialog
    var settingsDialog = new VmSettingsDialog(options);
    if (await settingsDialog.ShowAsync() == ContentDialogResult.Primary)
    {
        _currentVm = await _qemuService.LaunchVmAsync(options, CancellationToken.None);
        IsVmRunning = true;
        _currentVm.Exited += (s, e) => IsVmRunning = false;
    }
}
```

---

## Phase 4: Testing & Polish (Week 3)

### 4.1 Test Matrix

| Scenario | WHPX | Expected Result |
|----------|------|-----------------|
| Windows 11 Pro, Hyper-V enabled | ✓ | Fast boot with WHPX |
| Windows 11 Home | ✗ | Slower TCG emulation works |
| GPT/UEFI disk | - | Boots with OVMF |
| MBR/BIOS disk | - | Boots with SeaBIOS |
| ARM64 host, x64 VHDX | ✗ | TCG emulation (very slow) |

### 4.2 Error Handling

- QEMU crash → Show error with log file location
- WHPX conflict (VirtualBox running) → Fallback to TCG with warning
- Insufficient memory → Reduce default VM memory
- VHDX locked → Error message about unmounting first

### 4.3 Documentation

- Add "Boot as VM" section to user guide
- Document WHPX enable steps for performance
- Note about Windows Home limitations

---

## Phase 5: Future Enhancements

### 5.1 Optional: QCOW2 Conversion

VHDX direct access in QEMU is slower than native formats. Offer optional conversion:

```csharp
await _qemuService.ConvertToQcow2Async(vhdxPath, qcow2Path, progress, ct);
// qemu-img convert -f vhdx -O qcow2 -p input.vhdx output.qcow2
```

### 5.2 Optional: Hyper-V Backend

For users with Hyper-V, offer native performance path:

```csharp
public interface IHyperVService
{
    bool IsHyperVAvailable();
    Task<string> CreateTemporaryVmAsync(string vhdxPath);
    Task StartVmAsync(string vmName);
    Task StopAndDeleteVmAsync(string vmName);
}
```

### 5.3 Optional: VirtIO Drivers

Bundle VirtIO Windows drivers for better disk/network performance in QEMU:
- Download from: https://fedorapeople.org/groups/virt/virtio-win/
- Mount as secondary CD-ROM drive
- User installs in guest if needed

### 5.4 Optional: Screenshot/Verification

Automate boot verification:
1. Boot VM headless
2. Wait for Windows login screen (detect via screenshot analysis)
3. Capture screenshot as proof of successful boot
4. Shutdown VM

---

## File Structure Summary

```
src/
├── Chronos.Core/
│   └── VirtualMachine/
│       ├── IQemuService.cs
│       ├── QemuService.cs
│       ├── QemuCommandBuilder.cs
│       ├── QemuProcess.cs
│       ├── VmLaunchOptions.cs
│       └── QemuDownloader.cs
├── Chronos.App/
│   └── ViewModels/
│       └── BrowseViewModel.cs (extended)
└── Views/
    └── Dialogs/
        ├── VmSettingsDialog.xaml
        └── QemuDownloadDialog.xaml
```

---

## Dependencies

| Package | Purpose | License |
|---------|---------|---------|
| None required | Using Process class for QEMU | - |

External downloads (on first use):
| Component | Size | License | Source |
|-----------|------|---------|--------|
| QEMU Windows | ~80MB | GPLv2 | qemu.weilnetz.de |
| OVMF firmware | Included | BSD | edk2 project |

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| QEMU download fails | Retry logic, manual download link |
| WHPX conflicts with VirtualBox | Detect and warn, fallback to TCG |
| ARM64 Windows can't run x64 VMs fast | Document limitation, TCG still works |
| VHDX corruption from VM | Default to read-only snapshot mode |
| Large download size | Lazy download, show size before confirm |

---

## Success Criteria

1. User can launch any VHDX backup as a VM from Chronos
2. VM boots to Windows login screen
3. Works on Windows Home (without WHPX, slower)
4. Works on Windows Pro with WHPX (fast)
5. Original VHDX remains unmodified
6. QEMU download is transparent and reliable
7. Clean shutdown without orphaned processes

---

## Timeline

| Week | Milestone |
|------|-----------|
| 1 | QEMU service, download logic, WHPX detection |
| 2 | VM launch, command builder, UI integration |
| 3 | Testing, error handling, documentation |
| 4 | Optional: QCOW2 conversion, Hyper-V backend |
