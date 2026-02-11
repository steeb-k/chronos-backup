<#
.SYNOPSIS
    Build script for Chronos release artifacts.

.DESCRIPTION
    Builds x64 and ARM64 versions, creates installers and ZIP files.

.PARAMETER Version
    Version number (e.g., "1.0.0"). Updates installer scripts automatically.

.PARAMETER SkipBuild
    Skip the dotnet publish step (use existing binaries).

.PARAMETER SkipInstaller
    Skip installer generation (only create ZIPs).

.EXAMPLE
    .\Build-Release.ps1 -Version "1.0.0"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$SkipBuild,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$DistDir = Join-Path $RepoRoot "dist"
$InstallerDir = Join-Path $RepoRoot "installer"

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
    # Build x64
    Write-Host "[2/5] Building x64 Release..." -ForegroundColor Yellow
    Push-Location $RepoRoot
    dotnet publish src/Chronos.App.csproj -c Release -r win-x64 --self-contained -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "x64 build failed" }
    Pop-Location
    Write-Host "  Done." -ForegroundColor Green

    # Build ARM64
    Write-Host "[3/5] Building ARM64 Release..." -ForegroundColor Yellow
    Push-Location $RepoRoot
    dotnet publish src/Chronos.App.csproj -c Release -r win-arm64 --self-contained -p:Platform=ARM64
    if ($LASTEXITCODE -ne 0) { throw "ARM64 build failed" }
    Pop-Location
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

if (Test-Path $x64PublishDir) {
    if (Test-Path $x64Zip) { Remove-Item $x64Zip }
    Compress-Archive -Path "$x64PublishDir\*" -DestinationPath $x64Zip -CompressionLevel Optimal
    Write-Host "  Created: $x64Zip" -ForegroundColor Green
} else {
    Write-Host "  Warning: x64 publish directory not found" -ForegroundColor Yellow
}

if (Test-Path $arm64PublishDir) {
    if (Test-Path $arm64Zip) { Remove-Item $arm64Zip }
    Compress-Archive -Path "$arm64PublishDir\*" -DestinationPath $arm64Zip -CompressionLevel Optimal
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
