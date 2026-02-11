# Chronos

<div align="center">

![Screenshot](screenshot.png)

**Disk Imaging Utility for Windows**

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)](https://www.microsoft.com/windows)
[![Architecture](https://img.shields.io/badge/Architecture-x86%20%7C%20x64%20%7C%20ARM64-green.svg)](https://github.com/steeb-k/chronos-backup)

</div>

## Overview

Chronos is an open-source disk imaging utility for Windows built with WinUI 3. It handles disk and partition backup, restore, cloning, verification, and mounting, with VSS support for live system backups.

### Features

- **WinUI 3 interface** with Mica/Acrylic backdrop
- **Full disk and partition backup** to VHDX
- **Disk and partition cloning** (direct disk-to-disk or partition-to-partition)
- **Zstandard compression** (configurable level)
- **VSS integration** for consistent snapshots of running systems
- **Image verification** with integrity checks and SHA-256 hashing
- **VHDX mounting** to browse image contents via a drive letter
- **x86, x64, and ARM64** support

## Requirements

- Windows 10 version 1809+ or Windows 11
- Administrator privileges (required for disk access)
- .NET 10 runtime (bundled with self-contained builds)

## Installation

### From Release

Download the latest self-contained build from [Releases](https://github.com/steeb-k/chronos-backup/releases). Extract and run `Chronos.App.exe` as administrator.

### Build from Source

#### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10 SDK (19041 or later)
- Visual Studio 2022 (optional, for IDE development)

#### Debug Build

```powershell
git clone https://github.com/steeb-k/chronos-backup.git
cd chronos-backup

# Build for x64
dotnet build Chronos.sln -p:Platform=x64

# Build for ARM64
dotnet build Chronos.sln -p:Platform=ARM64

# Build for x86
dotnet build Chronos.sln -p:Platform=x86
```

Output location: `src/bin/<Platform>/Debug/net10.0-windows10.0.19041.0/`

#### Release Build (Self-Contained)

Self-contained builds include the .NET runtime and can run on machines without .NET installed.

```powershell
# x64 Release
dotnet publish src/Chronos.App.csproj -c Release -r win-x64 --self-contained -p:Platform=x64

# ARM64 Release
dotnet publish src/Chronos.App.csproj -c Release -r win-arm64 --self-contained -p:Platform=ARM64
```

Output location: `src/bin/Release/net10.0-windows10.0.19041.0/win-<rid>/publish/`

#### Creating Installers and Portable ZIPs

Use the release build script to generate installers (requires [Inno Setup 6+](https://jrsoftware.org/isdl.php)) and portable ZIP files:

```powershell
.\scripts\Build-Release.ps1 -Version "1.0.0"
```

This creates in the `dist/` folder:
- `Chronos-1.0.0-x64-Setup.exe` — Windows installer for x64
- `Chronos-1.0.0-arm64-Setup.exe` — Windows installer for ARM64
- `Chronos-1.0.0-x64-Portable.zip` — Portable ZIP for x64
- `Chronos-1.0.0-arm64-Portable.zip` — Portable ZIP for ARM64

Options:
- `-SkipBuild` — Use existing binaries (skip `dotnet publish`)
- `-SkipInstaller` — Only create ZIP files (skip Inno Setup)

#### Run from Source

```powershell
# Run x64 debug build
dotnet run --project src/Chronos.App.csproj -p:Platform=x64

# Run ARM64 debug build
dotnet run --project src/Chronos.App.csproj -p:Platform=ARM64
```

> **Note:** Cross-compilation works (e.g., building ARM64 on x64), but you must run the binary on matching hardware.

## Usage

### Backup

1. Navigate to the **Backup** page
2. Select a source disk and optionally a specific partition (or leave as "Entire Disk" for a full disk image)
3. Choose a destination path for the VHDX file
4. Configure options (VSS, verify after backup)
5. Click **Start Backup**

### Clone

1. Navigate to the **Clone** page
2. Select a source disk and optionally a specific partition
3. Select a destination disk (or a specific target partition for partition-to-partition clones)
4. Click **Start Clone**

### Restore

1. Navigate to the **Restore** page
2. Select a VHDX image file
3. Choose a target disk or specific partition
4. Click **Start Restore**

### Verify

1. Navigate to the **Verify** page
2. Select a VHDX image file
3. Click **Verify** to run an integrity check
4. Optionally compute a SHA-256 hash

### Mount

1. Navigate to the **Mount** page
2. Select a VHDX image file
3. Optionally toggle **Mount read-only**
4. Click **Mount** — the image is attached as a drive letter
5. Click **Dismount** when finished

## Architecture

```
Chronos.App       — WinUI 3 application (MVVM, CommunityToolkit.Mvvm)
Chronos.Core      — Imaging engines, compression, VSS, verification
Chronos.Native    — P/Invoke wrappers for Win32 disk and volume APIs
Chronos.Common    — Shared utilities and extensions
```

### Key Technologies

| Component | Purpose |
|-----------|---------|
| WinUI 3 | UI framework |
| .NET 10 | Runtime |
| VSS | Volume Shadow Copy for live snapshots |
| VHDX | Image format (native Windows virtual disk) |
| Zstandard | Compression |
| CommunityToolkit.Mvvm | MVVM infrastructure |

## Logs

Log files are written to:

```
%LOCALAPPDATA%\Chronos\Logs\chronos-YYYYMMDD.log
```

Logs include operation details and Win32 error codes. Common codes: 3 = path not found, 5 = access denied, 87 = invalid parameter.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

### Development Setup

1. Install Visual Studio 2022 with the **.NET desktop development** and **Windows application development** workloads
2. Clone the repository
3. Open `Chronos.sln`
4. Build and run

## License

GNU General Public License v3.0 — see [LICENSE](LICENSE).

## Roadmap

### Current
- Full disk and partition backup/restore
- Disk and partition cloning
- VSS integration
- Zstandard compression
- Image verification and SHA-256 hashing
- VHDX mounting

### Planned
- Incremental and differential backups
- Scheduled backups
- Bootable recovery media (WinPE)
- Encryption

## Links

- [Report a bug](https://github.com/steeb-k/chronos-backup/issues)
- [Request a feature](https://github.com/steeb-k/chronos-backup/issues)

## Acknowledgments

- [WinUI](https://github.com/microsoft/microsoft-ui-xaml)
- [Zstandard](https://github.com/facebook/zstd)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
