<#
.SYNOPSIS
    Build and run the Chronos development build from the repo root.

.PARAMETER Platform
    Target platform: x64 (default) or ARM64.

.PARAMETER NoBuild
    Skip the build step and run the last built binary.

.EXAMPLE
    .\run-dev.ps1
    .\run-dev.ps1 -Platform ARM64
    .\run-dev.ps1 -NoBuild
#>

param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot
$Project  = Join-Path $RepoRoot "src\Chronos.App.csproj"
$ExePath  = Join-Path $RepoRoot "src\bin\$Platform\Debug\net10.0-windows10.0.19041.0\win-$($Platform.ToLower())\Chronos.App.exe"

# Re-launch as admin if not already elevated — the app manifest requires it.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Platform $Platform"
    if ($NoBuild) { $argList += " -NoBuild" }
    Write-Host "Relaunching as Administrator..."
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    exit
}

# Build
if (-not $NoBuild) {
    Write-Host "Building Chronos ($Platform, Debug)..."
    dotnet build "$Project" -p:Platform=$Platform -c Debug --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed."
        exit $LASTEXITCODE
    }
}

# Verify binary exists
if (-not (Test-Path $ExePath)) {
    Write-Error "Binary not found: $ExePath`nRun without -NoBuild to build first."
    exit 1
}

Write-Host "Starting $ExePath"
Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path $ExePath)
