# Chronos — Windows PE Deployment Guide

Chronos runs inside **PhoenixPE** (Win11 22H2 / build 22621) as a fully self-contained
WinUI 3 application. This guide covers deployment, the built-in self-test, known
limitations, and troubleshooting.

---

## Table of Contents

1. [Quick Start](#quick-start)
2. [Self-Test Mode](#self-test-mode)
3. [Architecture Overview](#architecture-overview)
4. [System DLL Dependencies](#system-dll-dependencies)
5. [Known Limitations in WinPE](#known-limitations-in-winpe)
6. [Capability Detection](#capability-detection)
7. [Diagnostic Scripts](#diagnostic-scripts)
8. [Troubleshooting](#troubleshooting)
9. [PhoenixPE Plugin Checklist](#phoenixpe-plugin-checklist)

---

## Quick Start

### 1. Build the portable release

```powershell
.\scripts\Build-Release.ps1
```

This produces `dist\Chronos-<ver>-x64-Portable.zip` (and ARM64 variant).

### 2. Extract to WinPE

Copy the extracted portable folder to your PE media — for example:

```
X:\Program Files\Chronos\
```

### 3. Ensure system DLLs are present

WinPE does not ship the full Windows DLL set. Chronos needs ~25 system DLLs
that must be injected into the PE image via your PE builder (PhoenixPE plugin)
or copied to `System32` manually. See [System DLL Dependencies](#system-dll-dependencies).

### 4. Launch

```cmd
"X:\Program Files\Chronos\Chronos.App.exe"
```

Or run the self-test first:

```cmd
"X:\Program Files\Chronos\Chronos.App.exe" --selftest
```

---

## Self-Test Mode

Chronos includes a headless `--selftest` mode that exercises every subsystem
the app depends on **without launching the GUI**. This is the fastest way to
verify a WinPE deployment is correctly configured.

### Usage

```cmd
Chronos.App.exe --selftest
```

### What it tests

| Category | Tests |
|----------|-------|
| **Environment** | `PeEnvironment.IsWinPE`, all `PeCapabilities` flags, AppData directory resolution |
| **Disk Enumeration** | IOCTL disk probing, geometry, storage device property (model name), full `DiskEnumerator` pipeline, partition enumeration |
| **Volume APIs** | Volume GUID enumeration, `GetVolumeInformation` per drive letter, `DriveInfo` enumeration, target drive detection (Fixed + Removable) |
| **VSS** | `VssService.IsVssAvailable()` — expected to fail in WinPE |
| **WMI** | 7 WMI class queries (`Win32_DiskDrive`, `Win32_DiskPartition`, `Win32_Volume`, `Win32_LogicalDisk`, `Win32_Service` for VSS, `Win32_ShadowCopy`, `Win32_ComputerSystem`) — skipped if `HasWmi = false` |
| **File I/O** | Write probe to BaseDirectory and AppData |
| **DLL Dependencies** | Checks 12 critical system DLLs in System32, 4 WinAppSDK DLLs in app directory |

### Output

Results go to both **stdout** and `chronos-selftest.log` (in the app directory).

**Exit code** = number of failed tests. `0` means all passed.

### Example output (WinPE)

```
╔══════════════════════════════════════════╗
║       Chronos Self-Test Diagnostics      ║
╚══════════════════════════════════════════╝

Time:           2026-02-14 10:30:00
Machine:        MININT-ABC123
OS:             Microsoft Windows NT 10.0.22621.0
64-bit Process: True
Architecture:   X64
BaseDirectory:  X:\Program Files\Chronos\

── ENVIRONMENT DETECTION ──────────────────

  IsWinPE = True
  HasWmi:              True
  HasVss:              False
  HasDwm:              True
  ...
  [PASS] PeEnvironment.IsWinPE
  [PASS] PeEnvironment.Capabilities

── DISK ENUMERATION ───────────────────────

  Physical disks found: 2
    PhysicalDrive0
    PhysicalDrive1
  [PASS] DiskApi.ProbePhysicalDiskIndices (IOCTL)
  ...

╔══════════════════════════════════════════╗
║  PASSED: 38    FAILED: 0     SKIPPED: 0  ║
╚══════════════════════════════════════════╝

✓ ALL TESTS PASSED
```

---

## Architecture Overview

Chronos adapts to WinPE automatically at runtime:

```
┌─────────────────────────────────────────────────────┐
│                    Chronos App                       │
│                                                     │
│  PeEnvironment.IsWinPE ──► capability detection     │
│                                                     │
│  ┌──────────────┐    ┌──────────────────────┐       │
│  │ Full Windows  │    │ WinPE                │       │
│  ├──────────────┤    ├──────────────────────┤       │
│  │ WMI disks     │    │ IOCTL fallback disks │       │
│  │ VSS snapshots │    │ Direct disk read     │       │
│  │ PNG logo      │    │ Vector XAML logo     │       │
│  │ Update check  │    │ Updates skipped      │       │
│  │ Shell dialogs │    │ Simplified UI        │       │
│  └──────────────┘    └──────────────────────┘       │
└─────────────────────────────────────────────────────┘
```

### Key adaptations

| Feature | Full Windows | WinPE |
|---------|-------------|-------|
| Disk enumeration | WMI (`Win32_DiskDrive`) | IOCTL (`PhysicalDriveN` probing) |
| Volume info | WMI + `GetVolumeInformation` | `GetVolumeInformation` P/Invoke only |
| Disk model name | WMI | `IOCTL_STORAGE_QUERY_PROPERTY` |
| Volume snapshots | VSS (`vssapi.dll`) | Unavailable — direct read |
| Logo rendering | PNG bitmap (WIC) | XAML vector Path elements (Direct2D) |
| App data path | `%LOCALAPPDATA%\Chronos` | Fallback chain: LocalAppData → exe dir → `X:\Chronos` → temp |
| Update checking | GitHub API | Skipped |
| Target drives | Fixed only | Fixed + Removable |

---

## System DLL Dependencies

WinPE is a minimal environment. The following DLLs must be present in `System32`
for Chronos (WinUI 3 / WinAppSDK) to function:

### Critical — App will not launch without these

| DLL | Purpose |
|-----|---------|
| `combase.dll` | COM/WinRT activation |
| `ole32.dll` | COM infrastructure |
| `oleaut32.dll` | OLE Automation |
| `dwmapi.dll` | Desktop Window Manager API |
| `dcomp.dll` | DirectComposition |
| `d3d11.dll` | Direct3D 11 |
| `dxgi.dll` | DirectX Graphics Infrastructure |
| `d2d1.dll` | Direct2D |
| `dwrite.dll` | DirectWrite text rendering |
| `WinTypes.dll` | WinRT type resolution |
| `coremessaging.dll` | Windows message dispatch |

### Required — Features break without these

| DLL | Purpose |
|-----|---------|
| `shcore.dll` | DPI scaling |
| `uxtheme.dll` | Visual theme engine |
| `TextShaping.dll` | Complex text layout |
| `TextInputFramework.dll` | Text input services |
| `InputHost.dll` | Input processing |
| `UIAutomationCore.dll` | Accessibility |
| `twinapi.appcore.dll` | App-model API set |
| `propsys.dll` | Property system |
| `profapi.dll` | User profile API |
| `userenv.dll` | User environment |
| `rometadata.dll` | WinRT metadata resolution |

### Optional — Gracefully degraded if absent

| DLL | Purpose | Impact if missing |
|-----|---------|-------------------|
| `WindowsCodecs.dll` | WIC bitmap decode | PNG/JPEG images crash render thread — vector logo used instead |
| `vssapi.dll` | Volume Shadow Copy | VSS unavailable — direct disk read used |
| `virtdisk.dll` | VHD/VHDX mount/create | VHDX features unavailable |

### How to provide them

**Option A — PhoenixPE plugin (recommended):**
Use `RequireFileEx,AppendList` in a PhoenixPE `.script` to inject DLLs from the
source Windows image into the PE build automatically.

**Option B — Manual copy:**
```powershell
# Run on the full Windows machine that matches your PE build version
.\scripts\Collect-PeSystemDeps.ps1
# Or extract from a WIM/ISO:
.\scripts\Extract-WimDeps.ps1 -MountedPath "D:\Windows"
```
Then copy the output `pe-deps\` folder contents into WinPE's `System32`.

> **Important:** DLL versions must match the PE base build (e.g., build 22621).
> Mixing DLLs from different Windows builds causes ordinal mismatch crashes.

---

## Known Limitations in WinPE

### WIC (Windows Imaging Component) is broken

Any attempt to decode a bitmap (PNG, JPEG, BMP) crashes the WinUI render thread.
`WindowsCodecs.dll` is present in some PE builds but its codec infrastructure is
incomplete.

**Mitigation:** The Chronos logo uses XAML `Path` geometry elements (pure Direct2D
vector rendering) instead of bitmap `Image` sources. The `ChronosLogo` UserControl
renders identically in both environments.

### VSS is unavailable

The Volume Shadow Copy Service does not run in WinPE. Live-volume backup requires
either unmounting the volume or accepting a non-crash-consistent snapshot.

**Mitigation:** `VssService.IsVssAvailable()` returns `false`, and the backup
workflow skips VSS when unavailable.

### WMI may or may not work

PhoenixPE *can* include WMI (`Winmgmt` service), but it depends on the PE build
configuration. Chronos detects WMI availability at startup and falls back to
IOCTL-based disk enumeration when WMI is absent.

### Shell file dialogs may be limited

`comdlg32.dll` Open/Save dialogs work in most PhoenixPE builds but may not be
available in stripped-down PE images. Chronos detects this via `PeCapabilities.HasShellDialogs`.

### No automatic updates

Update checking is skipped when `PeEnvironment.IsWinPE == true`.

### RAM disk constraints

WinPE typically boots to a RAM disk (`X:\`). Available storage is limited by
system RAM. Large backup images should target an external drive, not the RAM disk.

---

## Capability Detection

Chronos probes the environment at startup via `PeEnvironment.Capabilities`:

```csharp
var caps = PeEnvironment.Capabilities;
// caps.HasWmi              — WMI service + wbemprox.dll present
// caps.HasVss              — VSS service + vssapi.dll present
// caps.HasDwm              — Desktop Window Manager running
// caps.HasVirtualDiskApi   — virtdisk.dll present
// caps.HasNetwork          — Network stack available
// caps.HasPersistentStorage — LocalAppData resolves
// caps.HasShellDialogs     — comdlg32.dll present
```

The self-test dumps all of these flags. If a capability is `false`, the
corresponding feature is gracefully disabled rather than crashing.

---

## Diagnostic Scripts

Four PowerShell scripts in `scripts/` help diagnose PE deployment issues:

| Script | Purpose | When to use |
|--------|---------|-------------|
| `Test-WinPE-Readiness.ps1` | Comprehensive 14-section readiness check | First deployment — run inside PE before launching Chronos |
| `Test-WinPE-Activation.ps1` | Deep-dive into WinRT/COM activation failures | When Chronos fails to launch with `COMException 0x80040111` |
| `Collect-PeSystemDeps.ps1` | Collect ~35 system DLLs from a full Windows install | Building DLL package on the dev machine |
| `Extract-WimDeps.ps1` | Extract system DLLs from a WIM/ISO image | When PE build version differs from dev machine |

### Example: Full readiness check

```powershell
# Inside WinPE, from the Chronos portable folder:
powershell -ExecutionPolicy Bypass -File .\scripts\Test-WinPE-Readiness.ps1 -ChronosPath .
```

---

## Troubleshooting

### App fails to launch — no window appears

1. Run `--selftest` to check DLL dependencies
2. Check `chronos-startup.log` in the app directory for native crash details
3. Run `Test-WinPE-Readiness.ps1` for comprehensive diagnostics

### `COMException 0x80040111` (CLASS_E_CLASSNOTAVAILABLE)

WinRT activation infrastructure is broken. Common causes:
- Missing `combase.dll`, `WinTypes.dll`, or `rometadata.dll`
- DWM service (`uxsms`) not running
- RPCSS / DcomLaunch services not started

Run `Test-WinPE-Activation.ps1` for detailed diagnosis.

### Disk enumeration returns 0 disks

- Check `--selftest` output for the IOCTL disk probing results
- Verify disk drivers are loaded in PE (`diskpart` → `list disk` to confirm)
- If WMI is present but returns 0, the IOCTL fallback should still find disks

### Logo appears blank / render crash

- WIC (bitmap) rendering is broken in WinPE — this is expected
- The vector `ChronosLogo` control should render correctly via Direct2D
- If the logo is blank, check that `d2d1.dll` and `dwrite.dll` are in System32

### "Access Denied" errors on disk operations

- Chronos requires **Administrator** privileges for raw disk access
- In WinPE, the default shell typically runs as SYSTEM (which has full access)
- If running under a restricted account, launch with `runas /user:Administrator`

### Build version mismatch crashes

If you see ordinal-not-found or entry-point-not-found errors:
- System DLLs in PE must match the PE base build number (e.g., 22621)
- Use `Extract-WimDeps.ps1 -MountedPath` pointed at the same Windows source
  your PE builder uses

---

## PhoenixPE Plugin Checklist

When building a PhoenixPE plugin for Chronos, ensure these components are included:

### Required PE Builder Components

- [x] **DWM** — Desktop Window Manager (`uxsms` service + `dwmapi.dll`, `dcomp.dll`)
- [x] **Direct3D** — `d3d11.dll`, `dxgi.dll`
- [x] **Direct2D / DirectWrite** — `d2d1.dll`, `dwrite.dll`
- [x] **COM / WinRT** — `combase.dll`, `ole32.dll`, `WinTypes.dll`, `rometadata.dll`
- [x] **RPCSS** — COM activation service

### Recommended PE Builder Components

- [ ] **WMI** — Enables WMI-based disk enumeration (falls back to IOCTL without)
- [ ] **Network Stack** — For update checks (skipped in PE anyway) and future network restore

### Not Needed

- VSS — Not functional in WinPE; Chronos handles this gracefully
- Windows Update — Not applicable
- Windows Search / Indexing — Not applicable

---

## Log Files

| File | Location | Content |
|------|----------|---------|
| `chronos-startup.log` | App directory | Startup diagnostics, DLL loading, crash details |
| `chronos-selftest.log` | App directory | Self-test results (when `--selftest` is used) |
| `Chronos.log` | AppData directory | Runtime application log (Serilog) |
