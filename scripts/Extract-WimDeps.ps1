<#
.SYNOPSIS
    Extract system DLLs from a Windows WIM/ISO that match the target PE build.

.DESCRIPTION
    Chronos bundles system DLLs (coremessaging.dll, InputHost.dll, etc.) that
    are not present in WinPE. These MUST come from the same Windows build as
    the PE to avoid ordinal/function mismatches with PE's system DLLs.

    This script extracts the needed DLLs from a Windows install.wim or
    mounted WIM image and places them in a pe-deps/ folder. The build script
    (Build-Release.ps1) then uses this folder instead of the dev machine's
    System32.

.PARAMETER WimPath
    Path to install.wim (or install.esd) from the Windows ISO used to
    build your PE. Requires running as Administrator for DISM mount.

.PARAMETER MountedPath
    Path to an already-mounted WIM or extracted Windows directory.
    For example: "E:\mount\Windows\System32" or a PhoenixPE workbench
    target directory. Does NOT require Administrator.

.PARAMETER OutputDir
    Where to place the extracted DLLs. Defaults to <repo>\pe-deps.

.PARAMETER WimIndex
    WIM image index to mount (default: 1). Use 'DISM /Get-WimInfo'
    to list available indices.

.EXAMPLE
    # From a mounted/extracted Windows directory (no admin needed):
    .\Extract-WimDeps.ps1 -MountedPath "E:\mount\Windows\System32"

    # From an install.wim (requires admin):
    .\Extract-WimDeps.ps1 -WimPath "D:\sources\install.wim"

    # PhoenixPE workbench target:
    .\Extract-WimDeps.ps1 -MountedPath "C:\PhoenixPE\Target\Windows\System32"
#>

param(
    [string]$WimPath,
    [string]$MountedPath,
    [string]$OutputDir,
    [int]$WimIndex = 1
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot "pe-deps"
}

# DLLs needed by WinAppSDK/WinUI 3 that WinPE typically lacks
$requiredDlls = @(
    "kernel.appcore.dll",
    "powrprof.dll",
    "WinTypes.dll",
    "shcore.dll",
    "rometadata.dll",
    "Microsoft.Internal.WarpPal.dll",
    "msvcp_win.dll",
    "coremessaging.dll",
    "CoreMessagingDataModel2.dll",
    "InputHost.dll",
    "ninput.dll",
    "windows.ui.dll",
    "twinapi.appcore.dll",
    "TextShaping.dll",
    "TextInputFramework.dll",
    "bcp47langs.dll",
    "mscms.dll",
    "profapi.dll",
    "userenv.dll",
    "propsys.dll",
    "urlmon.dll",
    "xmllite.dll",
    "iertutil.dll",
    "UIAutomationCore.dll",
    "WindowsCodecs.dll"
)

function Extract-FromDirectory {
    param([string]$SysDir)

    if (-not (Test-Path $SysDir)) {
        Write-Error "Directory not found: $SysDir"
        return
    }

    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir | Out-Null
    }

    $found = 0
    $missing = 0
    foreach ($dll in $requiredDlls) {
        $source = Join-Path $SysDir $dll
        if (Test-Path $source) {
            $dest = Join-Path $OutputDir $dll
            Copy-Item $source $dest -Force
            $ver = (Get-Item $source).VersionInfo.FileVersion
            Write-Host "  OK: $dll ($ver)" -ForegroundColor Green
            $found++
        } else {
            Write-Host "  MISSING: $dll (not in source)" -ForegroundColor Yellow
            $missing++
        }
    }

    Write-Host ""
    Write-Host "Extracted $found DLLs to: $OutputDir" -ForegroundColor Cyan
    if ($missing -gt 0) {
        Write-Host "$missing DLLs not found in source (may not be needed)" -ForegroundColor Yellow
    }

    # Show version of a key DLL to confirm build match
    $cmCheck = Join-Path $OutputDir "coremessaging.dll"
    if (Test-Path $cmCheck) {
        $ver = (Get-Item $cmCheck).VersionInfo.FileVersion
        Write-Host ""
        Write-Host "Source build: $ver" -ForegroundColor Cyan
        Write-Host "Ensure this matches your WinPE's OS build." -ForegroundColor Cyan
    }
}

# Determine source directory
if ($MountedPath) {
    # Direct path provided (mounted WIM, extracted dir, or PhoenixPE target)
    $sysDir = $MountedPath
    # If they gave us the Windows root, look for System32
    if ((Test-Path (Join-Path $sysDir "System32")) -and
        -not (Test-Path (Join-Path $sysDir "kernel32.dll"))) {
        $sysDir = Join-Path $sysDir "System32"
    }
    Write-Host "Extracting from: $sysDir" -ForegroundColor Cyan
    Write-Host ""
    Extract-FromDirectory -SysDir $sysDir
}
elseif ($WimPath) {
    # Mount WIM with DISM
    if (-not (Test-Path $WimPath)) {
        Write-Error "WIM file not found: $WimPath"
        exit 1
    }

    # Check for admin
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Error "Mounting a WIM requires running as Administrator. Use -MountedPath instead if you have an already-mounted image."
        exit 1
    }

    $mountDir = Join-Path $env:TEMP "chronos-wim-mount"
    if (-not (Test-Path $mountDir)) {
        New-Item -ItemType Directory -Path $mountDir | Out-Null
    }

    Write-Host "Mounting WIM index $WimIndex from: $WimPath" -ForegroundColor Cyan
    Write-Host "Mount point: $mountDir" -ForegroundColor DarkGray
    try {
        & dism /Mount-Wim /WimFile:$WimPath /Index:$WimIndex /MountDir:$mountDir /ReadOnly /Quiet
        if ($LASTEXITCODE -ne 0) {
            Write-Error "DISM mount failed with exit code $LASTEXITCODE"
            exit 1
        }

        $sysDir = Join-Path $mountDir "Windows\System32"
        Write-Host ""
        Extract-FromDirectory -SysDir $sysDir
    }
    finally {
        Write-Host ""
        Write-Host "Unmounting WIM..." -ForegroundColor DarkGray
        & dism /Unmount-Wim /MountDir:$mountDir /Discard /Quiet
        if (Test-Path $mountDir) {
            Remove-Item $mountDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
else {
    Write-Host "Extract system DLLs from a Windows image matching your WinPE build." -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  # From a mounted/extracted Windows directory (no admin):" -ForegroundColor DarkGray
    Write-Host '  .\Extract-WimDeps.ps1 -MountedPath "E:\mount\Windows\System32"' -ForegroundColor White
    Write-Host ""
    Write-Host "  # From install.wim (requires admin):" -ForegroundColor DarkGray
    Write-Host '  .\Extract-WimDeps.ps1 -WimPath "D:\sources\install.wim"' -ForegroundColor White
    Write-Host ""
    Write-Host "  # PhoenixPE workbench:" -ForegroundColor DarkGray
    Write-Host '  .\Extract-WimDeps.ps1 -MountedPath "C:\PhoenixPE\Target\Windows\System32"' -ForegroundColor White
    Write-Host ""
    Write-Host "After extraction, rebuild with:" -ForegroundColor Yellow
    Write-Host '  .\Build-Release.ps1 -PeDepsSource "pe-deps"' -ForegroundColor White
    exit 0
}
