<#
.SYNOPSIS
    Diagnostic script to troubleshoot why Chronos will not launch in a WinPE environment.

.DESCRIPTION
    Run this script INSIDE your PhoenixPE/WinPE environment to identify missing
    dependencies. It checks for .NET runtime, Windows App SDK / WinUI 3 prerequisites,
    DLL dependencies, COM registration, WMI availability, and more.

    Copy this script and the Chronos portable folder into your PE image, then run:
        powershell -ExecutionPolicy Bypass -File Test-WinPE-Readiness.ps1 -ChronosPath "X:\Chronos"

.PARAMETER ChronosPath
    Path to the extracted Chronos portable folder (where Chronos.App.exe lives).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Test-WinPE-Readiness.ps1 -ChronosPath "X:\Tools\Chronos"
#>

param(
    [Parameter(Mandatory = $false)]
    [string]$ChronosPath = "."
)

$ErrorActionPreference = "Continue"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Write-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = "")
    if ($Passed) {
        Write-Host "  [PASS] " -ForegroundColor Green -NoNewline
    } else {
        Write-Host "  [FAIL] " -ForegroundColor Red -NoNewline
    }
    Write-Host $Name -NoNewline
    if ($Detail -ne "") {
        Write-Host " -- $Detail" -ForegroundColor DarkGray
    } else {
        Write-Host ""
    }
    return $Passed
}

function Write-Warn {
    param([string]$Name, [string]$Detail = "")
    Write-Host "  [WARN] " -ForegroundColor Yellow -NoNewline
    Write-Host $Name -NoNewline
    if ($Detail -ne "") {
        Write-Host " -- $Detail" -ForegroundColor DarkGray
    } else {
        Write-Host ""
    }
}

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "== $Title ==" -ForegroundColor Cyan
}

$script:totalChecks = 0
$script:passedChecks = 0
$script:failedChecks = 0
$script:criticalFails = @()

function Test-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = "", [switch]$Critical)
    $script:totalChecks++
    if ($Passed) {
        $script:passedChecks++
    } else {
        $script:failedChecks++
        if ($Critical) { $script:criticalFails += $Name }
    }
    Write-Check -Name $Name -Passed $Passed -Detail $Detail
}

# ---------------------------------------------------------------------------
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Chronos WinPE Readiness Diagnostic" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------------------
Write-Section "Environment Detection"
# ---------------------------------------------------------------------------

$isWinPE = $false
$minWinPE = Test-Path "X:\Windows\System32\startnet.cmd"
$regWinPE = $false
try {
    $regVal = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WinPE" -ErrorAction SilentlyContinue
    $regWinPE = $null -ne $regVal
} catch {}
$isWinPE = $minWinPE -or $regWinPE

if ($isWinPE) {
    Write-Host "  Environment: Windows PE" -ForegroundColor Yellow
} else {
    Write-Host "  Environment: Full Windows (not WinPE)" -ForegroundColor Green
    Write-Host "  NOTE: Some checks are only meaningful when run inside WinPE." -ForegroundColor DarkGray
}

$osInfo = [System.Environment]::OSVersion
Write-Host "  OS Version:  $($osInfo.Version)" -ForegroundColor DarkGray
Write-Host "  Architecture: $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" -ForegroundColor DarkGray
Write-Host "  64-bit OS:   $([Environment]::Is64BitOperatingSystem)" -ForegroundColor DarkGray
Write-Host "  64-bit Proc: $([Environment]::Is64BitProcess)" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Write-Section "Chronos Application Files"
# ---------------------------------------------------------------------------

$exePath = Join-Path $ChronosPath "Chronos.App.exe"
$exeExists = Test-Path $exePath
Test-Check "Chronos.App.exe exists" $exeExists "Path: $exePath" -Critical

if ($exeExists) {
    $exeInfo = Get-Item $exePath
    Write-Host "    Size: $([math]::Round($exeInfo.Length / 1MB, 2)) MB" -ForegroundColor DarkGray

    $hostfxr = Test-Path (Join-Path $ChronosPath "hostfxr.dll")
    $coreclr = Test-Path (Join-Path $ChronosPath "coreclr.dll")
    $hostpolicy = Test-Path (Join-Path $ChronosPath "hostpolicy.dll")
    Test-Check "hostfxr.dll (self-contained runtime)" $hostfxr "" -Critical
    Test-Check "coreclr.dll (.NET CoreCLR)" $coreclr "" -Critical
    Test-Check "hostpolicy.dll (.NET host policy)" $hostpolicy "" -Critical
}

# ---------------------------------------------------------------------------
Write-Section "Windows App SDK / WinUI 3 Dependencies"
# ---------------------------------------------------------------------------

$criticalSdkDlls = @(
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.ui.xaml.dll",
    "Microsoft.WindowsAppRuntime.Bootstrap.dll"
)

$allSdkPresent = $true
foreach ($dll in $criticalSdkDlls) {
    $dllPath = Join-Path $ChronosPath $dll
    $exists = Test-Path $dllPath
    Test-Check $dll $exists "" -Critical
    if (-not $exists) { $allSdkPresent = $false }
}

# Check for *.pri resource files (needed for XAML Controls)
$priFiles = Get-ChildItem -Path $ChronosPath -Filter "*.pri" -ErrorAction SilentlyContinue
$priCount = ($priFiles | Measure-Object).Count
$hasPri = $priCount -gt 0
Test-Check "XAML resource files (*.pri)" $hasPri "Found $priCount .pri file(s)"

$resourcesPri = Test-Path (Join-Path $ChronosPath "resources.pri")
Test-Check "resources.pri" $resourcesPri ""

# ---------------------------------------------------------------------------
Write-Section "Visual C++ Runtime (UCRT / VCRuntime)"
# ---------------------------------------------------------------------------

$vcRuntimeDlls = @(
    "vcruntime140.dll",
    "vcruntime140_1.dll",
    "msvcp140.dll",
    "msvcp140_1.dll",
    "msvcp140_2.dll"
)

$ucrtDlls = @(
    "ucrtbase.dll",
    "api-ms-win-crt-runtime-l1-1-0.dll",
    "api-ms-win-crt-heap-l1-1-0.dll",
    "api-ms-win-crt-string-l1-1-0.dll",
    "api-ms-win-crt-stdio-l1-1-0.dll",
    "api-ms-win-crt-math-l1-1-0.dll",
    "api-ms-win-crt-locale-l1-1-0.dll",
    "api-ms-win-crt-convert-l1-1-0.dll"
)

Write-Host "  Checking VC++ Runtime (in app folder or System32):" -ForegroundColor DarkGray
foreach ($dll in $vcRuntimeDlls) {
    $inApp = Test-Path (Join-Path $ChronosPath $dll)
    $inSys = Test-Path (Join-Path "$env:SystemRoot\System32" $dll)
    $found = $inApp -or $inSys
    $location = "MISSING"
    if ($inApp) { $location = "app folder" }
    elseif ($inSys) { $location = "System32" }
    Test-Check $dll $found $location -Critical
}

Write-Host "  Checking Universal CRT:" -ForegroundColor DarkGray
foreach ($dll in $ucrtDlls) {
    $inApp = Test-Path (Join-Path $ChronosPath $dll)
    $inSys = Test-Path (Join-Path "$env:SystemRoot\System32" $dll)
    $found = $inApp -or $inSys
    $location = "MISSING"
    if ($inApp) { $location = "app folder" }
    elseif ($inSys) { $location = "System32" }
    Test-Check $dll $found $location -Critical
}

# ---------------------------------------------------------------------------
Write-Section "Windows OS Components"
# ---------------------------------------------------------------------------

# DWM (Desktop Window Manager) - required for WinUI 3 composition
$dwmService = Get-Service -Name "uxsms" -ErrorAction SilentlyContinue
$dwmRunning = $dwmService -and $dwmService.Status -eq "Running"
Test-Check "Desktop Window Manager (DWM) service running" $dwmRunning "Service: uxsms - WinUI 3 REQUIRES DWM" -Critical

$dwmApi = Test-Path "$env:SystemRoot\System32\dwmapi.dll"
Test-Check "dwmapi.dll present" $dwmApi ""

# DirectComposition - used by WinUI 3 for visual tree
$dcomp = Test-Path "$env:SystemRoot\System32\dcomp.dll"
Test-Check "dcomp.dll (DirectComposition)" $dcomp "Required for WinUI 3 visual composition"

# D3D11 - WinUI 3 uses Direct3D for rendering
$d3d11 = Test-Path "$env:SystemRoot\System32\d3d11.dll"
Test-Check "d3d11.dll (Direct3D 11)" $d3d11 "WinUI 3 rendering backend"

# DXGI
$dxgi = Test-Path "$env:SystemRoot\System32\dxgi.dll"
Test-Check "dxgi.dll (DXGI)" $dxgi ""

# WinTypes.dll - needed for WinRT activation
$winTypes = Test-Path "$env:SystemRoot\System32\WinTypes.dll"
Test-Check "WinTypes.dll (WinRT type system)" $winTypes "Required for WinRT class activation" -Critical

# combase.dll - COM/WinRT activation core
$combase = Test-Path "$env:SystemRoot\System32\combase.dll"
Test-Check "combase.dll (COM/WinRT core)" $combase "" -Critical

# WinRT API surface
$winrtApis = @(
    "api-ms-win-core-winrt-l1-1-0.dll",
    "api-ms-win-core-winrt-string-l1-1-0.dll",
    "api-ms-win-core-winrt-error-l1-1-0.dll"
)
foreach ($dll in $winrtApis) {
    $exists = Test-Path "$env:SystemRoot\System32\$dll"
    Test-Check $dll $exists "WinRT activation API"
}

# virtdisk.dll - needed for VHDX operations
$virtdisk = Test-Path "$env:SystemRoot\System32\virtdisk.dll"
Test-Check "virtdisk.dll (Virtual Disk API)" $virtdisk "Required for VHDX mount/create"

# ---------------------------------------------------------------------------
Write-Section "COM / WinRT Activation"
# ---------------------------------------------------------------------------

$winrtWorks = $false
try {
    [Windows.Foundation.PropertyValue, Windows.Foundation.FoundationContract, ContentType=WindowsRuntime] | Out-Null
    $winrtWorks = $true
} catch {
    try {
        Add-Type -AssemblyName "System.Runtime.InteropServices" -ErrorAction SilentlyContinue
        $winrtWorks = $null -ne [System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeMarshal]
    } catch {}
}
Test-Check "WinRT activation infrastructure" $winrtWorks "Can the system activate WinRT classes?"

$rpcss = Get-Service -Name "RPCSS" -ErrorAction SilentlyContinue
$rpcssRunning = $rpcss -and $rpcss.Status -eq "Running"
Test-Check "RPC Endpoint Mapper (RPCSS) service" $rpcssRunning "Required for COM activation"

# ---------------------------------------------------------------------------
Write-Section "WMI Availability (Disk Enumeration)"
# ---------------------------------------------------------------------------

$wmiService = Get-Service -Name "Winmgmt" -ErrorAction SilentlyContinue
$wmiRunning = $wmiService -and $wmiService.Status -eq "Running"
Test-Check "WMI service (Winmgmt) running" $wmiRunning "Required for disk enumeration"

if ($wmiRunning) {
    $wmiClasses = @(
        @{ Class = "Win32_DiskDrive"; NS = "root\CIMV2" },
        @{ Class = "Win32_DiskPartition"; NS = "root\CIMV2" },
        @{ Class = "Win32_Volume"; NS = "root\CIMV2" },
        @{ Class = "Win32_LogicalDisk"; NS = "root\CIMV2" },
        @{ Class = "MSFT_Disk"; NS = "root\Microsoft\Windows\Storage" }
    )

    foreach ($wmi in $wmiClasses) {
        $works = $false
        $count = 0
        try {
            $results = Get-CimInstance -ClassName $wmi.Class -Namespace $wmi.NS -ErrorAction Stop
            $count = ($results | Measure-Object).Count
            $works = $count -gt 0
        } catch {}
        Test-Check "WMI: $($wmi.Class)" $works "Returned $count instance(s)"
    }
} else {
    Write-Warn "Skipping WMI class checks (service not running)"
    Write-Host "    Chronos uses WMI to enumerate disks. Without WMI, disk enumeration" -ForegroundColor DarkGray
    Write-Host "    will fall back to IOCTL-only mode or fail." -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
Write-Section "VSS (Volume Shadow Copy)"
# ---------------------------------------------------------------------------

$vssService = Get-Service -Name "VSS" -ErrorAction SilentlyContinue
$vssAvailable = $null -ne $vssService
$vssRunning = $vssService -and $vssService.Status -eq "Running"
Test-Check "VSS service exists" $vssAvailable "Not required - Chronos has a fallback"
if ($vssAvailable) {
    Test-Check "VSS service running" $vssRunning "Can start on demand"
}

$vssApiDll = Test-Path "$env:SystemRoot\System32\vssapi.dll"
Test-Check "vssapi.dll present" $vssApiDll "Required for VSS snapshots, not critical"

# ---------------------------------------------------------------------------
Write-Section "File System and Storage"
# ---------------------------------------------------------------------------

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$hasLocalAppData = -not [string]::IsNullOrEmpty($localAppData)
Test-Check "LocalApplicationData folder resolves" $hasLocalAppData "Path: $localAppData" -Critical

if ($hasLocalAppData) {
    $canWrite = $false
    try {
        $testDir = Join-Path $localAppData "Chronos"
        [System.IO.Directory]::CreateDirectory($testDir) | Out-Null
        $testFile = Join-Path $testDir ".winpe-test"
        [System.IO.File]::WriteAllText($testFile, "test")
        Remove-Item $testFile -Force
        $canWrite = $true
    } catch {}
    Test-Check "Can write to LocalAppData" $canWrite ""
} else {
    Write-Warn "LocalAppData is empty" "Chronos uses this for logs, settings, and history. Needs fallback."
    Write-Host "    SUGGESTION: Set LOCALAPPDATA env var before launching Chronos:" -ForegroundColor Yellow
    Write-Host '    $env:LOCALAPPDATA = "X:\AppData"' -ForegroundColor White
}

# ---------------------------------------------------------------------------
Write-Section "Graphics / Display"
# ---------------------------------------------------------------------------

$hasDisplay = $false
try {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class DisplayCheck {
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
    public static bool HasDisplay() {
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        int w = GetDeviceCaps(dc, 8);
        int h = GetDeviceCaps(dc, 10);
        ReleaseDC(IntPtr.Zero, dc);
        return w > 0 && h > 0;
    }
}
"@ -ErrorAction SilentlyContinue
    $hasDisplay = [DisplayCheck]::HasDisplay()
} catch {}
Test-Check "Display device available" $hasDisplay "WinUI 3 needs a display"

$user32 = Test-Path "$env:SystemRoot\System32\user32.dll"
Test-Check "user32.dll (windowing)" $user32 ""

# ---------------------------------------------------------------------------
Write-Section "Attempt to Load Chronos.App.exe (Dry Run)"
# ---------------------------------------------------------------------------

if ($exeExists) {
    Write-Host "  Attempting to start Chronos with error capture..." -ForegroundColor DarkGray

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $false
    $psi.WorkingDirectory = $ChronosPath
    $psi.EnvironmentVariables["DOTNET_EnableDiagnostics"] = "1"
    $psi.EnvironmentVariables["WINUI_DISABLE_MICA"] = "1"

    $launchResult = "Unknown"
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
        $exited = $proc.WaitForExit(8000)

        if ($exited) {
            $exitCode = $proc.ExitCode
            $stderr = $proc.StandardError.ReadToEnd()
            $stdout = $proc.StandardOutput.ReadToEnd()

            if ($exitCode -eq 0) {
                $launchResult = "Exited cleanly (code 0) window may have appeared briefly"
            } else {
                $launchResult = "Crashed with exit code $exitCode"
            }

            if ($stderr) {
                Write-Host "  STDERR output:" -ForegroundColor Red
                Write-Host "    $stderr" -ForegroundColor DarkGray
            }
            if ($stdout) {
                Write-Host "  STDOUT output:" -ForegroundColor DarkGray
                Write-Host "    $stdout" -ForegroundColor DarkGray
            }
        } else {
            $launchResult = "Still running after 8s - app may have launched successfully!"
            try { $proc.Kill() } catch {}
        }
    } catch {
        $launchResult = "Failed to start: $($_.Exception.Message)"
    }

    Write-Host ""
    $resultColor = "Red"
    if ($launchResult -match "running|cleanly") { $resultColor = "Green" }
    Write-Host "  Launch result: $launchResult" -ForegroundColor $resultColor

    $wer = Get-ChildItem -Path "$env:TEMP" -Filter "*.dmp" -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 3
    if ($wer) {
        Write-Host "  Recent crash dumps found in TEMP:" -ForegroundColor Yellow
        foreach ($dump in $wer) {
            Write-Host "    $($dump.Name) - $($dump.LastWriteTime)" -ForegroundColor DarkGray
        }
    }
} else {
    Write-Warn "Skipping launch test (exe not found)"
}

# ---------------------------------------------------------------------------
Write-Section "Missing DLL Analysis"
# ---------------------------------------------------------------------------

$nativeDeps = @(
    "api-ms-win-core-com-l1-1-0.dll",
    "api-ms-win-core-processthreads-l1-1-0.dll",
    "api-ms-win-core-synch-l1-2-0.dll",
    "api-ms-win-core-memory-l1-1-0.dll",
    "api-ms-win-core-file-l1-1-0.dll",
    "api-ms-win-core-handle-l1-1-0.dll",
    "api-ms-win-core-registry-l1-1-0.dll",
    "api-ms-win-core-localization-l1-2-0.dll",
    "api-ms-win-core-shlwapi-legacy-l1-1-0.dll",
    "api-ms-win-shcore-scaling-l1-1-1.dll",
    "d2d1.dll",
    "dwrite.dll",
    "d3d11.dll",
    "dcomp.dll",
    "dwmapi.dll",
    "dxgi.dll",
    "WindowsCodecs.dll",
    "WinTypes.dll",
    "twinapi.appcore.dll"
)

$missingNative = @()
foreach ($dll in $nativeDeps) {
    $inSys = Test-Path "$env:SystemRoot\System32\$dll"
    if (-not $inSys) {
        $missingNative += $dll
    }
}

if ($missingNative.Count -eq 0) {
    Write-Host "  All checked native dependencies present in System32" -ForegroundColor Green
} else {
    Write-Host "  Missing from System32:" -ForegroundColor Red
    foreach ($dll in $missingNative) {
        Write-Host "    X $dll" -ForegroundColor Red
    }
}

# ---------------------------------------------------------------------------
Write-Section "Native DLL Load Probe (LoadLibraryEx)"
# ---------------------------------------------------------------------------
# This directly calls the Windows loader on key DLLs to determine which one
# actually fails when the OS tries to resolve transitive dependencies.

Write-Host "  Using LoadLibraryEx to probe each critical native DLL..." -ForegroundColor DarkGray
Write-Host ""

$loadLibType = @"
using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
public class NativeLoader {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    public static string TryLoad(string path) {
        IntPtr h = LoadLibraryEx(path, IntPtr.Zero, 0);
        if (h != IntPtr.Zero) {
            FreeLibrary(h);
            return null;
        }
        int err = Marshal.GetLastWin32Error();
        return new Win32Exception(err).Message + " (0x" + err.ToString("X") + ")";
    }
}
"@

try {
    Add-Type -TypeDefinition $loadLibType -ErrorAction Stop
} catch {
    # Type may already be loaded from a previous run
    if ($_.Exception.Message -notmatch "already exists") {
        Write-Host "  Could not compile P/Invoke helper: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# Probe DLLs in order: VC++ runtime, UCRT, system deps, then WinAppSDK natives
$probeDlls = @(
    # VC++ runtime (in app folder)
    @{ Name = "vcruntime140.dll";                         Path = (Join-Path $ChronosPath "vcruntime140.dll") },
    @{ Name = "vcruntime140_1.dll";                       Path = (Join-Path $ChronosPath "vcruntime140_1.dll") },
    @{ Name = "msvcp140.dll";                             Path = (Join-Path $ChronosPath "msvcp140.dll") },

    # UCRT base (in System32 or app folder)
    @{ Name = "ucrtbase.dll";                             Path = "$env:SystemRoot\System32\ucrtbase.dll" },

    # UCRT forwarders (in app folder)
    @{ Name = "api-ms-win-crt-runtime-l1-1-0.dll";       Path = (Join-Path $ChronosPath "api-ms-win-crt-runtime-l1-1-0.dll") },
    @{ Name = "api-ms-win-crt-heap-l1-1-0.dll";          Path = (Join-Path $ChronosPath "api-ms-win-crt-heap-l1-1-0.dll") },
    @{ Name = "api-ms-win-crt-stdio-l1-1-0.dll";         Path = (Join-Path $ChronosPath "api-ms-win-crt-stdio-l1-1-0.dll") },
    @{ Name = "api-ms-win-crt-string-l1-1-0.dll";        Path = (Join-Path $ChronosPath "api-ms-win-crt-string-l1-1-0.dll") },
    @{ Name = "api-ms-win-crt-math-l1-1-0.dll";          Path = (Join-Path $ChronosPath "api-ms-win-crt-math-l1-1-0.dll") },

    # System DLLs that WinAppSDK needs (may be missing from PE System32)
    @{ Name = "kernel.appcore.dll (appmodel APIs)";       Path = "$env:SystemRoot\System32\kernel.appcore.dll" },
    @{ Name = "powrprof.dll (power APIs)";                Path = "$env:SystemRoot\System32\powrprof.dll" },
    @{ Name = "WinTypes.dll (WinRT type resolution)";     Path = "$env:SystemRoot\System32\WinTypes.dll" },
    @{ Name = "shcore.dll (scaling/feature staging)";      Path = "$env:SystemRoot\System32\shcore.dll" },
    @{ Name = "rometadata.dll (WinRT metadata)";          Path = "$env:SystemRoot\System32\rometadata.dll" },
    @{ Name = "dcomp.dll (DirectComposition)";            Path = "$env:SystemRoot\System32\dcomp.dll" },
    @{ Name = "dwmapi.dll (DWM)";                         Path = "$env:SystemRoot\System32\dwmapi.dll" },
    @{ Name = "d2d1.dll (Direct2D)";                      Path = "$env:SystemRoot\System32\d2d1.dll" },
    @{ Name = "d3d11.dll (Direct3D 11)";                  Path = "$env:SystemRoot\System32\d3d11.dll" },
    @{ Name = "dwrite.dll (DirectWrite)";                  Path = "$env:SystemRoot\System32\dwrite.dll" },
    @{ Name = "dxgi.dll (DXGI)";                          Path = "$env:SystemRoot\System32\dxgi.dll" },
    @{ Name = "coremessaging.dll (input/messaging)";      Path = "$env:SystemRoot\System32\coremessaging.dll" },
    @{ Name = "ninput.dll (pointer input)";               Path = "$env:SystemRoot\System32\ninput.dll" },
    @{ Name = "windows.ui.dll (Windows.UI runtime)";      Path = "$env:SystemRoot\System32\windows.ui.dll" },
    @{ Name = "twinapi.appcore.dll (app core)";           Path = "$env:SystemRoot\System32\twinapi.appcore.dll" },
    @{ Name = "bcp47langs.dll (language tags)";           Path = "$env:SystemRoot\System32\bcp47langs.dll" },
    @{ Name = "WindowsCodecs.dll (WIC)";                  Path = "$env:SystemRoot\System32\WindowsCodecs.dll" },
    @{ Name = "xmllite.dll (XML parser)";                 Path = "$env:SystemRoot\System32\xmllite.dll" },

    # WinAppSDK native DLLs (in app folder) - test these LAST
    @{ Name = "microsoft.internal.frameworkudk.dll";      Path = (Join-Path $ChronosPath "microsoft.internal.frameworkudk.dll") },
    @{ Name = "Microsoft.WindowsAppRuntime.Bootstrap.dll"; Path = (Join-Path $ChronosPath "Microsoft.WindowsAppRuntime.Bootstrap.dll") },
    @{ Name = "Microsoft.WindowsAppRuntime.dll";          Path = (Join-Path $ChronosPath "Microsoft.WindowsAppRuntime.dll") },
    @{ Name = "Microsoft.ui.xaml.dll";                    Path = (Join-Path $ChronosPath "Microsoft.ui.xaml.dll") }
)

$loadFailed = @()
foreach ($entry in $probeDlls) {
    if (-not (Test-Path $entry.Path)) {
        Write-Host "  [SKIP] $($entry.Name) -- file not found at path" -ForegroundColor Yellow
        continue
    }
    $err = [NativeLoader]::TryLoad($entry.Path)
    if ($null -eq $err) {
        Write-Host "  [LOAD] $($entry.Name) -- OK" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] $($entry.Name) -- $err" -ForegroundColor Red
        $loadFailed += $entry.Name
    }
}

if ($loadFailed.Count -gt 0) {
    Write-Host ""
    Write-Host "  DLLs that failed LoadLibraryEx:" -ForegroundColor Red
    foreach ($n in $loadFailed) {
        Write-Host "    X $n" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "  Tip: The FIRST DLL that fails is usually the root cause." -ForegroundColor Yellow
    Write-Host "  Its dependencies are what is actually missing from the PE." -ForegroundColor Yellow
} else {
    Write-Host ""
    Write-Host "  All probed DLLs loaded successfully." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
Write-Section "Services Summary"
# ---------------------------------------------------------------------------

$relevantServices = @("uxsms", "RPCSS", "DcomLaunch", "Winmgmt", "VSS", "VDS")
Write-Host "  Service               Status" -ForegroundColor DarkGray
Write-Host "  --------------------  ----------" -ForegroundColor DarkGray
foreach ($svcName in $relevantServices) {
    $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
    if ($svc) {
        $color = "DarkGray"
        if ($svc.Status -eq "Running") { $color = "Green" }
        elseif ($svc.Status -eq "Stopped") { $color = "Yellow" }
        $displayName = $svc.DisplayName
        if ($displayName.Length -gt 20) { $displayName = $displayName.Substring(0, 20) }
        Write-Host "  $($displayName.PadRight(22)) $($svc.Status)" -ForegroundColor $color
    } else {
        Write-Host "  $($svcName.PadRight(22)) NOT FOUND" -ForegroundColor Red
    }
}

# ---------------------------------------------------------------------------
# SUMMARY
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  RESULTS: $script:passedChecks passed, $script:failedChecks failed, $script:totalChecks total" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

if ($script:criticalFails.Count -gt 0) {
    Write-Host ""
    Write-Host "  CRITICAL FAILURES (must be resolved):" -ForegroundColor Red
    foreach ($fail in $script:criticalFails) {
        Write-Host "    X $fail" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "  TROUBLESHOOTING GUIDE:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  1. If VC++ Runtime DLLs are missing:" -ForegroundColor White
Write-Host "     In PhoenixPE, enable the Visual C++ Runtime or VCRedist plugin" -ForegroundColor DarkGray
Write-Host "     Or copy vcruntime140.dll + msvcp140.dll into the Chronos folder" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  2. If UCRT (api-ms-win-crt-*) DLLs are missing:" -ForegroundColor White
Write-Host "     In PhoenixPE, enable Universal CRT or UCRT component" -ForegroundColor DarkGray
Write-Host "     These should come from Windows\System32 in the PE image" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  3. If DWM service is not running:" -ForegroundColor White
Write-Host "     WinUI 3 REQUIRES Desktop Window Manager for rendering" -ForegroundColor DarkGray
Write-Host "     In PhoenixPE, ensure Desktop Window Manager or DWM is enabled" -ForegroundColor DarkGray
Write-Host "     Try: Start-Service uxsms (if the service exists but is not running)" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  4. If WinRT activation fails:" -ForegroundColor White
Write-Host "     Ensure WinTypes.dll and combase.dll are in System32" -ForegroundColor DarkGray
Write-Host "     The WinRT API-set DLLs (api-ms-win-core-winrt-*) must be present" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  5. If DirectComposition / D3D / DXGI are missing:" -ForegroundColor White
Write-Host "     In PhoenixPE, enable DirectX or Direct3D components" -ForegroundColor DarkGray
Write-Host "     WinUI 3 uses D3D11 + DirectComposition for its rendering pipeline" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  6. If LocalAppData is empty:" -ForegroundColor White
Write-Host "     Before launching Chronos, set the environment variable:" -ForegroundColor DarkGray
Write-Host '     $env:LOCALAPPDATA = "X:\ProgramData"' -ForegroundColor DarkGray
Write-Host ""
Write-Host "  7. If WMI (Winmgmt) is not available:" -ForegroundColor White
Write-Host "     In PhoenixPE, enable WMI support" -ForegroundColor DarkGray
Write-Host "     Without WMI, Chronos will fall back to IOCTL-only disk enumeration" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  8. If the app crashes immediately with no error:" -ForegroundColor White
Write-Host "     Try running from command prompt to see error output" -ForegroundColor DarkGray
Write-Host "     Set DOTNET_EnableDiagnostics=1 before launching" -ForegroundColor DarkGray
Write-Host "     Check for .dmp files in TEMP" -ForegroundColor DarkGray
Write-Host "     Try: Chronos.App.exe 2>&1 | Tee-Object -FilePath chronos-launch.log" -ForegroundColor DarkGray
Write-Host ""

# ---------------------------------------------------------------------------
Write-Section "PhoenixPE Plugin Checklist"
# ---------------------------------------------------------------------------

Write-Host "  Ensure these PhoenixPE plugins/components are ENABLED in your build:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  Required:" -ForegroundColor White
Write-Host "    [x] Visual C++ Redistributable (2015-2022)" -ForegroundColor DarkGray
Write-Host "    [x] Universal CRT (UCRT)" -ForegroundColor DarkGray
Write-Host "    [x] Desktop Window Manager (DWM)" -ForegroundColor DarkGray
Write-Host "    [x] DirectX / Direct3D 11" -ForegroundColor DarkGray
Write-Host "    [x] DirectComposition (dcomp.dll)" -ForegroundColor DarkGray
Write-Host "    [x] COM / DCOM runtime" -ForegroundColor DarkGray
Write-Host "    [x] WinRT / Windows Runtime base" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Recommended:" -ForegroundColor White
Write-Host "    [x] WMI (Windows Management Instrumentation)" -ForegroundColor DarkGray
Write-Host "    [x] VDS (Virtual Disk Service)" -ForegroundColor DarkGray
Write-Host "    [x] PowerShell (you already have this)" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Optional (Graceful degradation without these):" -ForegroundColor White
Write-Host "    [ ] VSS (Volume Shadow Copy) - live backups will not work" -ForegroundColor DarkGray
Write-Host "    [ ] Network stack - update checks will not work" -ForegroundColor DarkGray
Write-Host ""
