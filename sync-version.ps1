# Sync version from version.json to all project files
# Usage: .\sync-version.ps1
# or to set a new version: .\sync-version.ps1 -Version "0.2.0" -GitHubTag "0.2.0-beta"

param(
    [string]$Version = "",
    [string]$GitHubTag = ""
)

$ErrorActionPreference = "Stop"
$ScriptRoot = $PSScriptRoot
if (-not $ScriptRoot) { $ScriptRoot = (Get-Location).Path }

# Read current version from version.json
$versionFile = Join-Path $ScriptRoot "version.json"
$versionData = Get-Content $versionFile | ConvertFrom-Json

if ($Version) {
    $versionData.version = $Version
}
if ($GitHubTag) {
    $versionData.gitHubTag = $GitHubTag
} elseif ($Version) {
    # Default gitHubTag to version if not specified
    $versionData.gitHubTag = $Version
}

$currentVersion = $versionData.version
$githubTag = $versionData.gitHubTag

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Chronos Version Sync" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Synchronizing version: $currentVersion" -ForegroundColor White
if ($githubTag) {
    Write-Host "GitHub tag: $githubTag" -ForegroundColor White
}
Write-Host ""

# Update version.json if parameters were provided
if ($Version -or $GitHubTag) {
    $versionData | ConvertTo-Json | Set-Content $versionFile
    Write-Host "[OK] Updated version.json" -ForegroundColor Green
}

# Update Version.props (MSBuild)
$propsContent = @"
<Project>
  <PropertyGroup>
    <AppVersion>$currentVersion</AppVersion>
  </PropertyGroup>
</Project>
"@
$propsPath = Join-Path $ScriptRoot "Version.props"
$propsContent | Set-Content $propsPath
Write-Host "[OK] Updated Version.props" -ForegroundColor Green

# Update Chronos.App.csproj
$csprojPath = Join-Path $ScriptRoot "src\Chronos.App.csproj"
if (Test-Path $csprojPath) {
    $csprojContent = Get-Content $csprojPath -Raw
    if ($csprojContent -match '<Version>[^<]*</Version>') {
        $csprojContent = $csprojContent -replace '<Version>[^<]*</Version>', "<Version>$currentVersion</Version>"
    }
    if ($csprojContent -match '<AssemblyVersion>[^<]*</AssemblyVersion>') {
        $csprojContent = $csprojContent -replace '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$currentVersion.0</AssemblyVersion>"
    }
    if ($csprojContent -match '<FileVersion>[^<]*</FileVersion>') {
        $csprojContent = $csprojContent -replace '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$currentVersion.0</FileVersion>"
    }
    $csprojContent | Set-Content $csprojPath -NoNewline
    Write-Host "[OK] Updated Chronos.App.csproj" -ForegroundColor Green
}

# Update app.manifest (convert semantic versioning to assembly format)
$assemblyVersion = "$currentVersion.0"
$manifestPath = Join-Path $ScriptRoot "src\app.manifest"
if (Test-Path $manifestPath) {
    $manifestContent = Get-Content $manifestPath -Raw
    $manifestContent = $manifestContent -replace '(<assemblyIdentity[^>]*\sversion=")[^"]*(")', "`${1}$assemblyVersion`${2}"
    $manifestContent | Set-Content $manifestPath -NoNewline
    Write-Host "[OK] Updated app.manifest (version: $assemblyVersion)" -ForegroundColor Green
}

# Update installer scripts
$issFiles = @(
    (Join-Path $ScriptRoot "installer\Chronos-x64.iss"),
    (Join-Path $ScriptRoot "installer\Chronos-arm64.iss")
)
foreach ($issFile in $issFiles) {
    if (Test-Path $issFile) {
        $issContent = Get-Content $issFile -Raw
        $issContent = $issContent -replace '#define MyAppVersion "[^"]*"', "#define MyAppVersion `"$currentVersion`""
        $issContent | Set-Content $issFile -NoNewline
        Write-Host "[OK] Updated $(Split-Path $issFile -Leaf)" -ForegroundColor Green
    }
}

# Update Build-Release.ps1 default parameter (make version optional with default)
$buildScriptPath = Join-Path $ScriptRoot "scripts\Build-Release.ps1"
if (Test-Path $buildScriptPath) {
    $buildScriptContent = Get-Content $buildScriptPath -Raw
    # Update default version value if pattern exists
    if ($buildScriptContent -match '\[string\]\$Version = "[^"]*"') {
        $buildScriptContent = $buildScriptContent -replace '\[string\]\$Version = "[^"]*"', "[string]`$Version = `"$currentVersion`""
        $buildScriptContent | Set-Content $buildScriptPath -NoNewline
        Write-Host "[OK] Updated Build-Release.ps1" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Version Sync Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Current version: $currentVersion" -ForegroundColor White
if ($githubTag) {
    Write-Host "GitHub tag: $githubTag" -ForegroundColor White
}
Write-Host ""
Write-Host "All project files have been updated." -ForegroundColor Yellow
Write-Host "Run 'scripts\Build-Release.ps1' to build release artifacts." -ForegroundColor Yellow
Write-Host ""
