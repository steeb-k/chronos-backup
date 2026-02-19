<#
.SYNOPSIS
    Collects system DLLs required by WinUI 3 / Windows App SDK that WinPE may not include.

.DESCRIPTION
    Run this script on a FULL Windows machine (not in PE). It copies the system DLLs
    that the Windows App SDK native layer depends on into a staging folder. You then
    copy the contents of that folder into your WinPE image's System32 directory
    (or into the Chronos portable folder alongside the EXE).

    The DLLs are identified by analyzing the PE import tables of all WinAppSDK native
    binaries in the Chronos publish directory.

.PARAMETER PublishDir
    Path to the Chronos publish directory containing the built application.

.PARAMETER OutputDir
    Path to write the collected system DLLs. Defaults to dist\pe-system-deps.

.EXAMPLE
    .\Collect-PeSystemDeps.ps1
    .\Collect-PeSystemDeps.ps1 -PublishDir ".\dist\Chronos" -OutputDir ".\dist\pe-deps"
#>

param(
    [string]$PublishDir = "",
    [string]$OutputDir  = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not $PublishDir) {
    $PublishDir = Join-Path $RepoRoot "src\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
}
if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot "dist\pe-system-deps"
}

if (-not (Test-Path $PublishDir)) {
    Write-Host "ERROR: Publish directory not found: $PublishDir" -ForegroundColor Red
    Write-Host "Run Build-Release.ps1 first." -ForegroundColor Yellow
    exit 1
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Chronos PE System Dependency Collector" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Publish dir: $PublishDir" -ForegroundColor DarkGray
Write-Host "Output dir:  $OutputDir" -ForegroundColor DarkGray
Write-Host ""

# Create output directory
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# -------------------------------------------------------------------------
# Phase 1: Extract PE import strings from all WinAppSDK native DLLs
# -------------------------------------------------------------------------
Write-Host "[1/3] Analyzing PE imports from WinAppSDK native DLLs..." -ForegroundColor Yellow

function Get-PeImports {
    param([string]$FilePath)
    $bytes = [System.IO.File]::ReadAllBytes($FilePath)
    $text  = [System.Text.Encoding]::ASCII.GetString($bytes)
    $ms    = [regex]::Matches($text, '[\w][\w\.\-]{3,60}\.dll')
    return ($ms | ForEach-Object { $_.Value.ToLower() } | Sort-Object -Unique)
}

# All native DLLs that ship with WinAppSDK
$nativeDlls = Get-ChildItem $PublishDir -Filter "*.dll" | Where-Object {
    $name = $_.Name.ToLower()
    ($name -match "^microsoft\.(windowsappruntime|internal\.framework|ui\.|ui\.xaml)") -or
    ($name -eq "windowsappruntime.deploymentextensions.onecore.dll")
}

$allImportedNames = @{}
foreach ($dll in $nativeDlls) {
    $imports = Get-PeImports $dll.FullName
    foreach ($imp in $imports) {
        if (-not $allImportedNames.ContainsKey($imp)) {
            $allImportedNames[$imp] = @()
        }
        $allImportedNames[$imp] += $dll.Name
    }
}

Write-Host "  Analyzed $($nativeDlls.Count) native DLLs, found $($allImportedNames.Count) unique imports" -ForegroundColor DarkGray

# -------------------------------------------------------------------------
# Phase 2: Identify system DLLs not in publish dir
# -------------------------------------------------------------------------
Write-Host "[2/3] Identifying system DLLs missing from publish directory..." -ForegroundColor Yellow

# System DLLs that WinPE likely already has (core OS)
$peBuiltins = @(
    "ntdll.dll", "kernel32.dll", "kernelbase.dll", "advapi32.dll",
    "ole32.dll", "oleaut32.dll", "rpcrt4.dll", "user32.dll",
    "gdi32.dll", "shell32.dll", "shlwapi.dll", "crypt32.dll",
    "imm32.dll", "version.dll"
)

# System DLLs that WinUI 3 needs and WinPE may NOT have.
# These are the implementing DLLs behind api-ms-win-* API sets
# plus directly-imported system DLLs.
$systemDllsToCollect = @(
    # API set implementing DLLs
    "kernel.appcore.dll",     # api-ms-win-appmodel-runtime-*
    "powrprof.dll",           # api-ms-win-power-*
    "WinTypes.dll",           # api-ms-win-ro-typeresolution-* (WinRT type resolution)
    "shcore.dll",             # api-ms-win-shcore-*, api-ms-win-core-featurestaging-*
    "rometadata.dll",         # WinRT metadata resolution

    # Graphics and composition
    "dcomp.dll",              # DirectComposition (compositing engine)
    "dwmapi.dll",             # Desktop Window Manager API
    "d2d1.dll",               # Direct2D
    "d3d11.dll",              # Direct3D 11
    "dwrite.dll",             # DirectWrite (text rendering)
    "dxgi.dll",               # DXGI (display/GPU infrastructure)

    # UI and windowing
    "coremessaging.dll",      # Core input/messaging (InputHost)
    "CoreMessagingDataModel2.dll", # Dependency of coremessaging
    "InputHost.dll",          # Input processing
    "ninput.dll",             # Pointer/touch input
    "windows.ui.dll",         # Windows.UI runtime
    "twinapi.appcore.dll",    # Modern app core APIs
    "uxtheme.dll",            # Theme rendering
    "TextShaping.dll",        # Text shaping for DirectWrite
    "TextInputFramework.dll", # Text input

    # Other dependencies
    "bcp47langs.dll",         # BCP47 language tag support
    "mscms.dll",              # Color management
    "profapi.dll",            # User profile API
    "userenv.dll",            # User environment
    "propsys.dll",            # Property system
    "urlmon.dll",             # URL moniker (used by XAML)
    "xmllite.dll",            # XML parser

    # Accessibility (WinUI queries these)
    "UIAutomationCore.dll",   # UI Automation

    # Extended API set backing DLLs
    "win32u.dll",             # NTUser private/window/sysparams (ext-ms-win-ntuser-*)
    "WindowsCodecs.dll"       # Windows Imaging Component
)

Write-Host "  Checking $($systemDllsToCollect.Count) system DLLs..." -ForegroundColor DarkGray

# -------------------------------------------------------------------------
# Phase 3: Copy DLLs from host System32
# -------------------------------------------------------------------------
Write-Host "[3/3] Copying system DLLs to output directory..." -ForegroundColor Yellow
Write-Host ""

$copied   = 0
$skipped  = 0
$notFound = 0

foreach ($dllName in $systemDllsToCollect) {
    $source = Join-Path "$env:SystemRoot\System32" $dllName
    $dest   = Join-Path $OutputDir $dllName

    if (Test-Path $source) {
        # Check if already in publish dir (no need to duplicate)
        $inPublish = Test-Path (Join-Path $PublishDir $dllName)
        if ($inPublish) {
            Write-Host "  [SKIP] $dllName (already in publish dir)" -ForegroundColor DarkGray
            $skipped++
        } else {
            Copy-Item $source $dest -Force
            $sizeKB = [math]::Round((Get-Item $dest).Length / 1KB, 0)
            Write-Host "  [COPY] $dllName ($sizeKB KB)" -ForegroundColor Green
            $copied++
        }
    } else {
        Write-Host "  [MISS] $dllName (not found in host System32)" -ForegroundColor Yellow
        $notFound++
    }
}

# Also collect UCRT forwarders from downlevel if not in publish dir
$downlevel = Join-Path "$env:SystemRoot" "System32\downlevel"
if (Test-Path $downlevel) {
    $forwarders = Get-ChildItem $downlevel -Filter "api-ms-win-crt-*.dll" -ErrorAction SilentlyContinue
    foreach ($fw in $forwarders) {
        $inPublish = Test-Path (Join-Path $PublishDir $fw.Name)
        if (-not $inPublish) {
            Copy-Item $fw.FullName (Join-Path $OutputDir $fw.Name) -Force
            $copied++
        }
    }
}

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Collection complete" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Copied:    $copied DLLs" -ForegroundColor Green
Write-Host "  Skipped:   $skipped DLLs (already in publish)" -ForegroundColor DarkGray
Write-Host "  Not found: $notFound DLLs" -ForegroundColor Yellow
Write-Host ""

$totalSizeKB = [math]::Round(
    (Get-ChildItem $OutputDir -Filter "*.dll" | Measure-Object -Property Length -Sum).Sum / 1KB, 0
)
$totalSizeMB = [math]::Round($totalSizeKB / 1024, 1)
Write-Host "  Output: $OutputDir" -ForegroundColor White
Write-Host "  Total size: $totalSizeMB MB ($totalSizeKB KB)" -ForegroundColor White
Write-Host ""
Write-Host "  NEXT STEPS:" -ForegroundColor Cyan
Write-Host "  1. Copy all DLLs from this folder into your WinPE System32:" -ForegroundColor White
Write-Host "       Copy-Item '$OutputDir\*' 'X:\Windows\System32\' -Force" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  2. OR copy them into the Chronos portable folder:" -ForegroundColor White
Write-Host "       Copy-Item '$OutputDir\*' '<ChronosPath>\' -Force" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  3. Then re-run Test-WinPE-Readiness.ps1 in the PE environment" -ForegroundColor White
