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

.PARAMETER Sign
    Sign executables and installers (non-interactive). Implies you want signing without a prompt.

.PARAMETER NoSign
    Do not sign (non-interactive). Overrides -Sign if both are specified.

.EXAMPLE
    .\Build-Release.ps1
    .\Build-Release.ps1 -Version "1.0.0"
    .\Build-Release.ps1 -Sign
#>

param(
    [string]$Version = "0.7.0",

    [switch]$SkipBuild,
    [switch]$SkipInstaller,
    [switch]$Sign,
    [switch]$NoSign
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

if ($NoSign) {
    $SignExecutables = $false
} elseif ($Sign) {
    $SignExecutables = $true
} else {
    $signResponse = Read-Host "Sign Chronos.App.exe (x64/ARM64) and setup exes with Azure Artifact Signing when configured? [y/N]"
    $SignExecutables = ($signResponse -match '^\s*y')
}

if ($SignExecutables) {
    Write-Host "Signing: enabled for this run." -ForegroundColor DarkGray
} else {
    Write-Host "Signing: skipped (unsigned build)." -ForegroundColor DarkGray
}
Write-Host ""

# Create dist directory
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir | Out-Null
}

# Sign files with Azure Artifact Signing (if metadata and tools exist). Call before ZIP/installer so packaged content is signed.
# Returns $true if signing was skipped or all signings succeeded; $false if any signing failed.
function Sign-WithArtifactSigning {
    param([string[]]$PathsToSign)
    $MetadataPath = if ($env:ARTIFACT_SIGNING_METADATA) { $env:ARTIFACT_SIGNING_METADATA } else { Join-Path $RepoRoot "artifact-signing-metadata.json" }
    if (-not (Test-Path $MetadataPath)) { return $true }

    $SignToolExe = $env:SIGNTOOL_PATH
    if (-not $SignToolExe -or -not (Test-Path $SignToolExe)) {
        $KitsBin = "C:\Program Files (x86)\Windows Kits\10\bin"
        if (Test-Path $KitsBin) {
            $Latest = Get-ChildItem $KitsBin -Directory | Where-Object { $_.Name -match "^\d+\.\d+\.\d+" } | Sort-Object { [version]($_.Name -replace "^(\d+\.\d+\.\d+).*", '$1') } -Descending | Select-Object -First 1
            if ($Latest) {
                $Candidate = Join-Path (Join-Path $KitsBin $Latest.Name) "x64\signtool.exe"
                if (Test-Path $Candidate) { $SignToolExe = $Candidate }
            }
        }
    }
    if (-not $SignToolExe) {
        $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if ($cmd) { $SignToolExe = $cmd.Source }
    }

    $DlibPath = $env:ARTIFACT_SIGNING_DLIB
    if (-not $DlibPath -or -not (Test-Path $DlibPath)) {
        $SearchRoots = @(
            "$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools",
            "$env:LOCALAPPDATA\Microsoft\ArtifactSigningTools",
            "$env:LOCALAPPDATA\Microsoft\TrustedSigningClientTools",
            "C:\ProgramData\Microsoft\MicrosoftTrustedSigningClientTools",
            "C:\Program Files\Microsoft\Azure Artifact Signing Client Tools",
            "C:\Program Files (x86)\Microsoft\Azure Artifact Signing Client Tools",
            "C:\Program Files (x86)\Windows Kits\AzureCodeSigning"
        )
        foreach ($root in $SearchRoots) {
            if (Test-Path $root) {
                $found = Get-ChildItem -Path $root -Recurse -Filter "Azure.CodeSigning.Dlib.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($found) { $DlibPath = $found.FullName; break }
            }
        }
    }

    if (-not $SignToolExe -or -not (Test-Path $SignToolExe) -or -not $DlibPath -or -not (Test-Path $DlibPath)) { return $true }

    $SignFailed = $false
    Write-Host ""
    Write-Host "Signing with Azure Artifact Signing..." -ForegroundColor Cyan
    foreach ($ExeToSign in $PathsToSign) {
        if (Test-Path $ExeToSign) {
            $name = Split-Path $ExeToSign -Leaf
            Write-Host "  Signing $name ..." -ForegroundColor Cyan
            $SignToolArgs = @("sign", "/v", "/fd", "SHA256", "/tr", "http://timestamp.acs.microsoft.com", "/td", "SHA256", "/dlib", $DlibPath, "/dmdf", $MetadataPath, $ExeToSign)
            & $SignToolExe $SignToolArgs
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "  Signing failed for $name (exit code $LASTEXITCODE)."
                $SignFailed = $true
            } else {
                Write-Host "  Signed $name" -ForegroundColor Green
            }
        }
    }
    return (-not $SignFailed)
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
        
        # Find .NET 10 runtime directory for the target architecture
        # Use NuGet package cache which has arch-specific runtime packs (works for cross-compilation)
        $rid = "win-$($Arch.ToLower())"
        $nugetPkgDir = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.netcore.app.runtime.$rid"
        $runtimeDir = $null

        if (Test-Path $nugetPkgDir) {
            $runtimeVersion = Get-ChildItem $nugetPkgDir -Directory -Filter "10.*" |
                Sort-Object { [version]$_.Name } -Descending |
                Select-Object -First 1
            if ($runtimeVersion) {
                $runtimeDir = Join-Path $runtimeVersion.FullName "runtimes\$rid\lib\net10.0"
            }
        }

        # Fallback to shared framework (only correct when host arch == target arch)
        if (-not $runtimeDir -or -not (Test-Path $runtimeDir)) {
            $sharedBase = "C:\Program Files\dotnet\shared\Microsoft.NETCore.App"
            $sharedDir = Get-ChildItem $sharedBase -Directory -Filter "10.*" |
                Sort-Object { [version]$_.Name } -Descending |
                Select-Object -First 1
            if ($sharedDir) {
                $runtimeDir = $sharedDir.FullName
                Write-Host "    WARNING: Using shared framework (host arch) for $Arch - verify this matches target" -ForegroundColor DarkYellow
            }
        }

        if ($runtimeDir -and (Test-Path $runtimeDir)) {
            # Assemblies that need to be replaced (facade/trimmed -> full implementation)
            $assembliesToFix = @(
                "System.Runtime.InteropServices.dll",  # Required for CsWinRT AOT vtable generation
                "System.Private.CoreLib.dll"           # Required for System.Environment.SetEnvironmentVariable
            )
            
            foreach ($asmName in $assembliesToFix) {
                $sourceAsm = Join-Path $runtimeDir $asmName
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

# Bundle PE runtime dependencies (UCRT forwarders + VC++ runtime DLLs)
# These are required for WinPE environments where the API set schema and system DLLs are incomplete.
# The forwarder DLLs redirect api-ms-win-crt-* imports to ucrtbase.dll, which WinPE does include.
#
# NOTE: Windows system DLLs required by WinUI 3 (coremessaging.dll, InputHost.dll, etc.)
# are NOT bundled here. They are injected by the Chronos PhoenixPE plugin using
# RequireFileEx to extract version-matched copies from the source WIM.
# See: Projects/PhoenixPE/Applications/Backup & Imaging/Chronos.script
function Bundle-PeRuntimeDeps {
    param([string]$PublishDir, [string]$Arch)

    if (-not (Test-Path $PublishDir)) { return }

    $hostArch = if ([Environment]::Is64BitOperatingSystem) { "x64" } else { "x86" }
    if ($Arch.ToLower() -ne $hostArch.ToLower()) {
        Write-Host "    Skipping PE runtime deps for $Arch (host is $hostArch, cross-compile)" -ForegroundColor DarkYellow
        Write-Host "    To support $Arch in WinPE, copy system DLLs from an $Arch system" -ForegroundColor DarkYellow
        return
    }

    $copied = 0
    $sys32 = Join-Path $env:SystemRoot "System32"
    $downlevelDir = Join-Path $sys32 "downlevel"

    # Copy UCRT forwarder DLLs (api-ms-win-crt-*.dll)
    if (Test-Path $downlevelDir) {
        $forwarders = Get-ChildItem $downlevelDir -Filter "api-ms-win-crt-*.dll" -ErrorAction SilentlyContinue
        foreach ($dll in $forwarders) {
            $dest = Join-Path $PublishDir $dll.Name
            if (-not (Test-Path $dest)) {
                Copy-Item $dll.FullName $dest -Force
                $copied++
            }
        }
    }

    # Copy VC++ runtime DLLs that may not be bundled by publish
    $vcrtDlls = @("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll")
    foreach ($dllName in $vcrtDlls) {
        $dest = Join-Path $PublishDir $dllName
        if (-not (Test-Path $dest)) {
            $source = Join-Path $sys32 $dllName
            if (Test-Path $source) {
                Copy-Item $source $dest -Force
                $copied++
            }
        }
    }

    # Remove any previously-bundled Windows system DLLs that must NOT be
    # shipped. These conflict with WinPE's own version-matched copies.
    # Bundling them from a different Windows build causes delay-load
    # failures (ERROR_PROC_NOT_FOUND) due to function mismatches.
    # The PhoenixPE Chronos plugin extracts these from the source WIM instead.
    $removeDlls = @(
        # Core graphics (WinPE has its own matched versions)
        "dcomp.dll", "dwmapi.dll", "d2d1.dll", "d3d11.dll", "dwrite.dll",
        "dxgi.dll", "uxtheme.dll", "win32u.dll", "VERSION.dll",

        # System DLLs now handled by the PhoenixPE plugin (RequireFileEx from WIM)
        "kernel.appcore.dll", "powrprof.dll", "WinTypes.dll", "shcore.dll",
        "rometadata.dll", "Microsoft.Internal.WarpPal.dll", "msvcp_win.dll",
        "coremessaging.dll", "CoreMessagingDataModel2.dll", "InputHost.dll",
        "ninput.dll", "windows.ui.dll", "twinapi.appcore.dll", "TextShaping.dll",
        "TextInputFramework.dll", "bcp47langs.dll", "mscms.dll", "profapi.dll",
        "userenv.dll", "propsys.dll", "urlmon.dll", "xmllite.dll", "iertutil.dll",
        "UIAutomationCore.dll", "WindowsCodecs.dll"
    )
    $removed = 0
    foreach ($dllName in $removeDlls) {
        $target = Join-Path $PublishDir $dllName
        if (Test-Path $target) {
            Remove-Item $target -Force -ErrorAction SilentlyContinue
            $removed++
        }
    }

    if ($removed -gt 0) {
        Write-Host "    Removed $removed system DLLs from $Arch (handled by PE plugin)" -ForegroundColor DarkGray
    }
    if ($copied -gt 0) {
        Write-Host "    Bundled $copied runtime DLLs for $Arch" -ForegroundColor DarkGray
    }
}

Write-Host "Bundling PE runtime dependencies..." -ForegroundColor Yellow
$x64PublishDir_pe = Join-Path $RepoRoot "src\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
$arm64PublishDir_pe = Join-Path $RepoRoot "src\bin\Release\net10.0-windows10.0.19041.0\win-arm64\publish"
Bundle-PeRuntimeDeps -PublishDir $x64PublishDir_pe -Arch "x64"
Bundle-PeRuntimeDeps -PublishDir $arm64PublishDir_pe -Arch "ARM64"
Write-Host "  Done." -ForegroundColor Green

# Sign app exes before packaging so ZIPs and installers contain signed executables (optional)
$AppSigningSucceeded = $true
if ($SignExecutables) {
    $AppSigningSucceeded = Sign-WithArtifactSigning -PathsToSign @(
        (Join-Path $x64PublishDir_pe "Chronos.App.exe"),
        (Join-Path $arm64PublishDir_pe "Chronos.App.exe")
    )
    if (-not $AppSigningSucceeded) {
        Write-Host ""
        $response = Read-Host "One or more signing steps failed. Continue building ZIPs and installers (unsigned)? [Y/n]"
        if ($response -match '^n|^N') {
            Write-Host "Build stopped by user." -ForegroundColor Yellow
            exit 1
        }
        Write-Host "Continuing with unsigned build..." -ForegroundColor Yellow
    }
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

# Optional: Sign installer exes (app exes were signed before packaging when signing was enabled)
if ($SignExecutables) {
    Sign-WithArtifactSigning -PathsToSign @(
        (Join-Path $DistDir "Chronos-$Version-x64-Setup.exe"),
        (Join-Path $DistDir "Chronos-$Version-arm64-Setup.exe")
    )
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
