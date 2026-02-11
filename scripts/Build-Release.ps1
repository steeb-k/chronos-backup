<#
.SYNOPSIS
    Build script for Chronos release artifacts.

.DESCRIPTION
    Builds x64 and ARM64 versions, creates installers and ZIP files.

.PARAMETER Version
    Version number (e.g., "1.0.0"). If not specified, reads from version.json.

.PARAMETER SkipBuild
    Skip the dotnet publish step (use existing binaries).

.PARAMETER SkipInstaller
    Skip installer generation (only create ZIPs).

.EXAMPLE
    .\Build-Release.ps1
    .\Build-Release.ps1 -Version "1.0.0"
#>

param(
    [string]$Version = "0.1.0",

    [switch]$SkipBuild,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$DistDir = Join-Path $RepoRoot "dist"
$InstallerDir = Join-Path $RepoRoot "installer"

# Read version from version.json if using default
$versionFile = Join-Path $RepoRoot "version.json"
if ((Test-Path $versionFile) -and ($Version -eq "0.1.1")) {
    $versionData = Get-Content $versionFile | ConvertFrom-Json
    $Version = $versionData.version
}

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  Chronos Release Build v$Version" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# Create dist directory
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir | Out-Null
}

# Update version in installer scripts
Write-Host "[1/5] Updating version in installer scripts..." -ForegroundColor Yellow
$issFiles = @(
    (Join-Path $InstallerDir "Chronos-x64.iss"),
    (Join-Path $InstallerDir "Chronos-arm64.iss")
)
foreach ($iss in $issFiles) {
    if (Test-Path $iss) {
        $content = Get-Content $iss -Raw
        $content = $content -replace '#define MyAppVersion ".*"', "#define MyAppVersion `"$Version`""
        Set-Content $iss $content -NoNewline
    }
}
Write-Host "  Done." -ForegroundColor Green

if (-not $SkipBuild) {
    # Function to fix self-contained deployment (replace facade/trimmed assemblies with full implementations)
    # This is required because WindowsAppSDK requires certain APIs that are trimmed from self-contained builds
    function Fix-SelfContainedAssemblies {
        param([string]$PublishDir, [string]$Arch)
        
        # Find .NET 10 runtime directory
        $runtimeBase = "C:\Program Files\dotnet\shared\Microsoft.NETCore.App"
        $runtimeDir = Get-ChildItem $runtimeBase -Directory -Filter "10.*" | 
            Sort-Object { [version]$_.Name } -Descending | 
            Select-Object -First 1
        
        if ($runtimeDir) {
            # Assemblies that need to be replaced (facade/trimmed -> full implementation)
            $assembliesToFix = @(
                "System.Runtime.InteropServices.dll",  # Required for CsWinRT AOT vtable generation
                "System.Private.CoreLib.dll"           # Required for System.Environment.SetEnvironmentVariable
            )
            
            foreach ($asmName in $assembliesToFix) {
                $sourceAsm = Join-Path $runtimeDir.FullName $asmName
                $destAsm = Join-Path $PublishDir $asmName
                
                if ((Test-Path $sourceAsm) -and (Test-Path $destAsm)) {
                    # Only replace if sizes differ (facade is smaller)
                    $sourceSize = (Get-Item $sourceAsm).Length
                    $destSize = (Get-Item $destAsm).Length
                    if ($sourceSize -gt $destSize) {
                        Copy-Item $sourceAsm $destAsm -Force
                        Write-Host "    Fixed $asmName ($destSize -> $sourceSize bytes)" -ForegroundColor DarkGray
                    }
                }
            }
            Write-Host "  Fixed self-contained assemblies for $Arch" -ForegroundColor DarkGray
        }
    }

    # Build x64
    Write-Host "[2/5] Building x64 Release..." -ForegroundColor Yellow
    Push-Location $RepoRoot
    dotnet publish src/Chronos.App.csproj -c Release -r win-x64 --self-contained -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "x64 build failed" }
    Pop-Location
    
    # Fix self-contained deployment issues for x64
    $x64Publish = Join-Path $RepoRoot "src\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
    Fix-SelfContainedAssemblies -PublishDir $x64Publish -Arch "x64"
    Write-Host "  Done." -ForegroundColor Green

    # Build ARM64
    Write-Host "[3/5] Building ARM64 Release..." -ForegroundColor Yellow
    Push-Location $RepoRoot
    dotnet publish src/Chronos.App.csproj -c Release -r win-arm64 --self-contained -p:Platform=ARM64
    if ($LASTEXITCODE -ne 0) { throw "ARM64 build failed" }
    Pop-Location
    
    # Fix self-contained deployment issues for ARM64
    $arm64Publish = Join-Path $RepoRoot "src\bin\Release\net10.0-windows10.0.19041.0\win-arm64\publish"
    Fix-SelfContainedAssemblies -PublishDir $arm64Publish -Arch "ARM64"
    Write-Host "  Done." -ForegroundColor Green
} else {
    Write-Host "[2/5] Skipping x64 build (--SkipBuild)" -ForegroundColor DarkGray
    Write-Host "[3/5] Skipping ARM64 build (--SkipBuild)" -ForegroundColor DarkGray
}

# Create ZIP files
Write-Host "[4/5] Creating portable ZIP files..." -ForegroundColor Yellow

$x64PublishDir = Join-Path $RepoRoot "src\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
$arm64PublishDir = Join-Path $RepoRoot "src\bin\Release\net10.0-windows10.0.19041.0\win-arm64\publish"

$x64Zip = Join-Path $DistDir "Chronos-$Version-x64-Portable.zip"
$arm64Zip = Join-Path $DistDir "Chronos-$Version-arm64-Portable.zip"

# Helper function to create ZIP excluding .facade files (which may be locked)
function Create-PortableZip {
    param([string]$SourceDir, [string]$DestZip)
    
    # Get all items except .facade files
    $items = Get-ChildItem -Path $SourceDir -Recurse | Where-Object { $_.Extension -ne '.facade' }
    
    # Create temp directory structure without .facade files
    $tempDir = Join-Path $env:TEMP "chronos-zip-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    
    # Copy non-facade files preserving structure
    foreach ($item in $items) {
        $relativePath = $item.FullName.Substring($SourceDir.Length + 1)
        $destPath = Join-Path $tempDir $relativePath
        if ($item.PSIsContainer) {
            New-Item -ItemType Directory -Path $destPath -Force -ErrorAction SilentlyContinue | Out-Null
        } else {
            $destDir = Split-Path $destPath -Parent
            if (-not (Test-Path $destDir)) {
                New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            }
            Copy-Item $item.FullName $destPath -Force
        }
    }
    
    # Create ZIP from temp directory
    if (Test-Path $DestZip) { Remove-Item $DestZip -Force }
    Compress-Archive -Path "$tempDir\*" -DestinationPath $DestZip -CompressionLevel Optimal
    
    # Cleanup temp
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

if (Test-Path $x64PublishDir) {
    Create-PortableZip -SourceDir $x64PublishDir -DestZip $x64Zip
    Write-Host "  Created: $x64Zip" -ForegroundColor Green
} else {
    Write-Host "  Warning: x64 publish directory not found" -ForegroundColor Yellow
}

if (Test-Path $arm64PublishDir) {
    Create-PortableZip -SourceDir $arm64PublishDir -DestZip $arm64Zip
    Write-Host "  Created: $arm64Zip" -ForegroundColor Green
} else {
    Write-Host "  Warning: ARM64 publish directory not found" -ForegroundColor Yellow
}

# Build installers
if (-not $SkipInstaller) {
    Write-Host "[5/5] Building installers with Inno Setup..." -ForegroundColor Yellow
    
    # Find Inno Setup compiler
    $isccPaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    
    $iscc = $isccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    
    if (-not $iscc) {
        Write-Host "  Warning: Inno Setup not found. Skipping installer generation." -ForegroundColor Yellow
        Write-Host "  Install from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    } else {
        # Build x64 installer
        $x64Iss = Join-Path $InstallerDir "Chronos-x64.iss"
        if ((Test-Path $x64Iss) -and (Test-Path $x64PublishDir)) {
            & $iscc $x64Iss
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  Created: Chronos-$Version-x64-Setup.exe" -ForegroundColor Green
            } else {
                Write-Host "  Warning: x64 installer build failed" -ForegroundColor Yellow
            }
        }
        
        # Build ARM64 installer
        $arm64Iss = Join-Path $InstallerDir "Chronos-arm64.iss"
        if ((Test-Path $arm64Iss) -and (Test-Path $arm64PublishDir)) {
            & $iscc $arm64Iss
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  Created: Chronos-$Version-arm64-Setup.exe" -ForegroundColor Green
            } else {
                Write-Host "  Warning: ARM64 installer build failed" -ForegroundColor Yellow
            }
        }
    }
} else {
    Write-Host "[5/5] Skipping installer generation (--SkipInstaller)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  Build complete!" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output directory: $DistDir" -ForegroundColor White
Get-ChildItem $DistDir | ForEach-Object {
    $size = "{0:N2} MB" -f ($_.Length / 1MB)
    Write-Host "  $($_.Name) ($size)" -ForegroundColor Gray
}
