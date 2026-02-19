<#
.SYNOPSIS
    Diagnose WinRT activation failures for Chronos in WinPE.

.DESCRIPTION
    This script specifically targets the COMException 0x80040111
    (CLASS_E_CLASSNOTAVAILABLE) error when WinUI 3 tries to activate
    Microsoft.UI.Xaml.Application in WinPE.

    It tests:
    - Activation context from the embedded manifest
    - DllGetActivationFactory on WinAppSDK DLLs
    - DWM (Desktop Window Manager) availability
    - SxS infrastructure
    - API set resolution for WinRT

.PARAMETER ChronosPath
    Path to the extracted Chronos portable folder.
#>

param(
    [Parameter(Mandatory = $false)]
    [string]$ChronosPath = "."
)

$ErrorActionPreference = "Continue"

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

function Write-Info {
    param([string]$Name, [string]$Detail = "")
    Write-Host "  [INFO] " -ForegroundColor Cyan -NoNewline
    Write-Host $Name -NoNewline
    if ($Detail -ne "") {
        Write-Host " -- $Detail" -ForegroundColor DarkGray
    } else {
        Write-Host ""
    }
}

# Resolve path
$ChronosPath = (Resolve-Path $ChronosPath -ErrorAction SilentlyContinue).Path
if (-not $ChronosPath) {
    Write-Host "ERROR: ChronosPath not found." -ForegroundColor Red
    exit 1
}
$exe = Join-Path $ChronosPath "Chronos.App.exe"
if (-not (Test-Path $exe)) {
    Write-Host "ERROR: Chronos.App.exe not found at $ChronosPath" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Chronos WinPE Activation Diagnostic" -ForegroundColor Cyan
Write-Host "  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor DarkGray
Write-Host "  Path: $ChronosPath" -ForegroundColor DarkGray
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# ===================================================================
# Section 1: DWM (Desktop Window Manager) Analysis
# ===================================================================
Write-Host "--- DWM (Desktop Window Manager) Analysis ---" -ForegroundColor Yellow
Write-Host ""

# Check if DWM-related files exist
$dwmFiles = @(
    @{ Name = "dwm.exe";        Path = "$env:SystemRoot\System32\dwm.exe" },
    @{ Name = "dwmcore.dll";    Path = "$env:SystemRoot\System32\dwmcore.dll" },
    @{ Name = "dwmapi.dll";     Path = "$env:SystemRoot\System32\dwmapi.dll" },
    @{ Name = "dwmredir.dll";   Path = "$env:SystemRoot\System32\dwmredir.dll" },
    @{ Name = "dcomp.dll";      Path = "$env:SystemRoot\System32\dcomp.dll" },
    @{ Name = "d3d11.dll";      Path = "$env:SystemRoot\System32\d3d11.dll" },
    @{ Name = "dxgi.dll";       Path = "$env:SystemRoot\System32\dxgi.dll" },
    @{ Name = "d2d1.dll";       Path = "$env:SystemRoot\System32\d2d1.dll" }
)

foreach ($f in $dwmFiles) {
    $exists = Test-Path $f.Path
    if ($exists) {
        $sz = (Get-Item $f.Path).Length
        Write-Check $f.Name $true "$sz bytes"
    } else {
        # Check in the Chronos folder as fallback
        $alt = Join-Path $ChronosPath $f.Name
        if (Test-Path $alt) {
            $sz = (Get-Item $alt).Length
            Write-Check $f.Name $true "in app dir ($sz bytes)"
        } else {
            Write-Check $f.Name $false "NOT FOUND"
        }
    }
}

# Check if DWM service exists and its state
Write-Host ""
$uxsms = Get-Service -Name "UxSms" -ErrorAction SilentlyContinue
if ($uxsms) {
    Write-Check "UxSms service (DWM)" $($uxsms.Status -eq "Running") "Status: $($uxsms.Status)"
} else {
    # Check registry for service definition
    $svcReg = Test-Path "HKLM:\SYSTEM\CurrentControlSet\Services\UxSms"
    if ($svcReg) {
        Write-Warn "UxSms service registered but not loaded"
    } else {
        Write-Check "UxSms service (DWM)" $false "Service not registered"
    }
}

# Check if DWM is actually running as a process
$dwmProc = Get-Process -Name "dwm" -ErrorAction SilentlyContinue
Write-Check "DWM process running" ($null -ne $dwmProc)

# DWM composition check via dwmapi
Write-Host ""
try {
    $dwmapiSig = @"
[DllImport("dwmapi.dll")]
public static extern int DwmIsCompositionEnabled(out bool enabled);
"@
    $dwmapi = Add-Type -MemberDefinition $dwmapiSig -Name "DwmCheck" -Namespace "WinPEDiag" -PassThru -ErrorAction Stop
    $enabled = $false
    $hr = $dwmapi::DwmIsCompositionEnabled([ref]$enabled)
    if ($hr -eq 0) {
        Write-Check "DWM composition enabled" $enabled
    } else {
        Write-Check "DWM composition" $false "DwmIsCompositionEnabled returned 0x$($hr.ToString('X8'))"
    }
} catch {
    Write-Check "DWM composition" $false "dwmapi.dll not loadable: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "  ** WinUI 3 REQUIRES DWM for rendering. If DWM is not running," -ForegroundColor White
Write-Host "     the app cannot display any UI even if activation succeeds. **" -ForegroundColor White
Write-Host ""

# ===================================================================
# Section 2: SxS / Activation Context Infrastructure
# ===================================================================
Write-Host "--- SxS / Activation Context Infrastructure ---" -ForegroundColor Yellow
Write-Host ""

# Check SxS-related system files
$sxsFiles = @(
    @{ Name = "sxs.dll";          Path = "$env:SystemRoot\System32\sxs.dll" },
    @{ Name = "sxstrace.exe";     Path = "$env:SystemRoot\System32\sxstrace.exe" },
    @{ Name = "combase.dll";      Path = "$env:SystemRoot\System32\combase.dll" },
    @{ Name = "WinTypes.dll";     Path = "$env:SystemRoot\System32\WinTypes.dll" },
    @{ Name = "rometadata.dll";   Path = "$env:SystemRoot\System32\rometadata.dll" },
    @{ Name = "twinapi.appcore.dll"; Path = "$env:SystemRoot\System32\twinapi.appcore.dll" }
)

foreach ($f in $sxsFiles) {
    $exists = Test-Path $f.Path
    if ($exists) {
        $sz = (Get-Item $f.Path).Length
        Write-Check $f.Name $true "$sz bytes"
    } else {
        $alt = Join-Path $ChronosPath $f.Name
        if (Test-Path $alt) {
            Write-Check $f.Name $true "in app dir only"
        } else {
            Write-Check $f.Name $false "NOT FOUND"
        }
    }
}

# Check WinSxS folder exists
$winsxs = "$env:SystemRoot\WinSxS"
$winsxsExists = Test-Path $winsxs
Write-Check "WinSxS folder" $winsxsExists
if ($winsxsExists) {
    $winsxsCount = (Get-ChildItem $winsxs -Directory -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Info "WinSxS subdirectories" "$winsxsCount"
}

Write-Host ""

# ===================================================================
# Section 3: Activation Context Test
# ===================================================================
Write-Host "--- Activation Context Test ---" -ForegroundColor Yellow
Write-Host ""

try {
    $actCtxCode = @"
using System;
using System.Runtime.InteropServices;

public class ActivationContextTest {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ACTCTXW {
        public int cbSize;
        public uint dwFlags;
        public string lpSource;
        public ushort wProcessorArchitecture;
        public ushort wLangId;
        public string lpAssemblyDirectory;
        public IntPtr lpResourceName;
        public string lpApplicationName;
        public IntPtr hModule;
    }

    public const uint ACTCTX_FLAG_SET_PROCESS_DEFAULT = 0x10;
    public const uint ACTCTX_FLAG_RESOURCE_NAME_VALID = 0x08;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateActCtxW(ref ACTCTXW pActCtx);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ActivateActCtx(IntPtr hActCtx, out IntPtr lpCookie);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeactivateActCtx(uint dwFlags, IntPtr lpCookie);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void ReleaseActCtx(IntPtr hActCtx);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentActCtx(out IntPtr lphActCtx);

    public static string TestManifest(string exePath) {
        ACTCTXW ctx = new ACTCTXW();
        ctx.cbSize = Marshal.SizeOf(typeof(ACTCTXW));
        ctx.dwFlags = ACTCTX_FLAG_RESOURCE_NAME_VALID;
        ctx.lpSource = exePath;
        ctx.lpResourceName = (IntPtr)1;  // CREATEPROCESS_MANIFEST_RESOURCE_ID

        IntPtr hActCtx = CreateActCtxW(ref ctx);
        if (hActCtx == (IntPtr)(-1)) {
            int err = Marshal.GetLastWin32Error();
            return "FAIL: CreateActCtxW error " + err + " (0x" + err.ToString("X") + ")";
        }

        IntPtr cookie;
        bool activated = ActivateActCtx(hActCtx, out cookie);
        if (!activated) {
            int err = Marshal.GetLastWin32Error();
            ReleaseActCtx(hActCtx);
            return "FAIL: ActivateActCtx error " + err;
        }

        // Success - deactivate and release
        DeactivateActCtx(0, cookie);
        ReleaseActCtx(hActCtx);
        return "OK: Activation context created and activated successfully";
    }

    public static string CheckProcessContext() {
        IntPtr hCtx;
        GetCurrentActCtx(out hCtx);
        if (hCtx == IntPtr.Zero || hCtx == (IntPtr)(-1)) {
            return "No process activation context";
        }
        return "Process has activation context: 0x" + hCtx.ToString("X");
    }
}
"@

    Add-Type -TypeDefinition $actCtxCode -ErrorAction Stop

    # Test process activation context
    $procCtx = [ActivationContextTest]::CheckProcessContext()
    Write-Info "Process context" $procCtx

    # Test creating activation context from the exe manifest
    $result = [ActivationContextTest]::TestManifest($exe)
    $passed = $result.StartsWith("OK")
    Write-Check "CreateActCtxW from exe manifest" $passed $result

} catch {
    Write-Check "Activation context test" $false $_.Exception.Message
}

Write-Host ""

# ===================================================================
# Section 4: WinRT Activation Factory Test
# ===================================================================
Write-Host "--- WinRT Activation Factory Test ---" -ForegroundColor Yellow
Write-Host ""

try {
    $roCode = @"
using System;
using System.Runtime.InteropServices;

public class WinRTActivationTest {
    [DllImport("combase.dll", PreserveSig = true)]
    public static extern int RoInitialize(int type);

    [DllImport("combase.dll", PreserveSig = true)]
    public static extern int RoGetActivationFactory(
        IntPtr classId,
        ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true)]
    public static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string str,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    public static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    // IActivationFactory IID
    public static readonly Guid IID_IActivationFactory =
        new Guid("00000035-0000-0000-C000-000000000046");

    public static string TryRoActivate(string className) {
        // Initialize WinRT
        int hr = RoInitialize(1); // RO_INIT_MULTITHREADED
        // hr=1 means already initialized, that is OK

        IntPtr hstring;
        hr = WindowsCreateString(className, className.Length, out hstring);
        if (hr != 0) return "FAIL: WindowsCreateString hr=0x" + hr.ToString("X8");

        Guid iid = IID_IActivationFactory;
        IntPtr factory;
        hr = RoGetActivationFactory(hstring, ref iid, out factory);
        WindowsDeleteString(hstring);

        if (hr == 0 && factory != IntPtr.Zero) {
            Marshal.Release(factory);
            return "OK";
        }
        return "FAIL: hr=0x" + hr.ToString("X8") + HrName(hr);
    }

    public static string TryDllGetActivationFactory(string dllPath, string className) {
        IntPtr hLib = LoadLibraryW(dllPath);
        if (hLib == IntPtr.Zero) {
            int err = Marshal.GetLastWin32Error();
            return "FAIL: LoadLibrary error " + err;
        }

        IntPtr proc = GetProcAddress(hLib, "DllGetActivationFactory");
        if (proc == IntPtr.Zero) {
            FreeLibrary(hLib);
            return "FAIL: DllGetActivationFactory export not found";
        }

        // We found the export - we cannot call it safely from PowerShell
        // because it requires HSTRING and IActivationFactory** params
        // but finding the export confirms the DLL supports WinRT activation
        FreeLibrary(hLib);
        return "OK: DllGetActivationFactory export found at 0x" + proc.ToString("X");
    }

    private static string HrName(int hr) {
        switch ((uint)hr) {
            case 0x80040111: return " (CLASS_E_CLASSNOTAVAILABLE)";
            case 0x80040154: return " (REGDB_E_CLASSNOTREG)";
            case 0x80070002: return " (ERROR_FILE_NOT_FOUND)";
            case 0x80004002: return " (E_NOINTERFACE)";
            case 0x80004005: return " (E_FAIL)";
            case 0x800401F3: return " (CO_E_CLASSSTRING)";
            default: return "";
        }
    }
}
"@

    Add-Type -TypeDefinition $roCode -ErrorAction Stop

    # Test 1: RoGetActivationFactory with OS WinRT type
    $r = [WinRTActivationTest]::TryRoActivate("Windows.Foundation.Uri")
    Write-Check "RoGetActivationFactory: Windows.Foundation.Uri" ($r -eq "OK") $r

    # Test 2: RoGetActivationFactory with DispatcherQueue (in CoreMessagingXP.dll)
    $r = [WinRTActivationTest]::TryRoActivate("Microsoft.UI.Dispatching.DispatcherQueueController")
    Write-Check "RoGetActivationFactory: DispatcherQueueController" ($r -eq "OK") $r

    # Test 3: RoGetActivationFactory with the actual class that fails
    $r = [WinRTActivationTest]::TryRoActivate("Microsoft.UI.Xaml.Application")
    Write-Check "RoGetActivationFactory: Microsoft.UI.Xaml.Application" ($r -eq "OK") $r

    # Test 4: RoGetActivationFactory with Compositor (in dcompi.dll)
    $r = [WinRTActivationTest]::TryRoActivate("Microsoft.UI.Composition.Compositor")
    Write-Check "RoGetActivationFactory: Compositor" ($r -eq "OK") $r

    # Test 5: RoGetActivationFactory with AppWindow (in Microsoft.UI.Windowing.dll)
    $r = [WinRTActivationTest]::TryRoActivate("Microsoft.UI.Windowing.AppWindow")
    Write-Check "RoGetActivationFactory: AppWindow" ($r -eq "OK") $r

    Write-Host ""

    # Test DllGetActivationFactory export on key DLLs
    Write-Host "  DllGetActivationFactory export check:" -ForegroundColor DarkGray
    $keyDlls = @(
        "Microsoft.ui.xaml.dll",
        "Microsoft.UI.Xaml.Controls.dll",
        "Microsoft.WindowsAppRuntime.dll",
        "CoreMessagingXP.dll",
        "dcompi.dll",
        "Microsoft.UI.Windowing.dll"
    )
    foreach ($dll in $keyDlls) {
        $dllPath = Join-Path $ChronosPath $dll
        if (Test-Path $dllPath) {
            $r = [WinRTActivationTest]::TryDllGetActivationFactory($dllPath, "")
            $passed = $r.StartsWith("OK")
            Write-Check "  $dll" $passed $r
        } else {
            Write-Check "  $dll" $false "NOT FOUND"
        }
    }

} catch {
    Write-Check "WinRT activation test" $false $_.Exception.Message
}

Write-Host ""

# ===================================================================
# Section 5: Native DLL Load Test (LoadLibrary)
# ===================================================================
Write-Host "--- Native DLL Load Test ---" -ForegroundColor Yellow
Write-Host ""

try {
    $loadCode = @"
using System;
using System.Runtime.InteropServices;

public class DllLoadTest {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryExW(string path, IntPtr hFile, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    public const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    public static string TryLoad(string path) {
        IntPtr h = LoadLibraryExW(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (h == IntPtr.Zero) {
            int err = Marshal.GetLastWin32Error();
            return "FAIL: error " + err + " (0x" + err.ToString("X") + ")";
        }
        FreeLibrary(h);
        return "OK";
    }
}
"@
    Add-Type -TypeDefinition $loadCode -ErrorAction Stop

    # Test loading key WinAppSDK native DLLs in dependency order
    $loadOrder = @(
        "Microsoft.WindowsAppRuntime.dll",
        "Microsoft.WindowsAppRuntime.Bootstrap.dll",
        "CoreMessagingXP.dll",
        "dcompi.dll",
        "Microsoft.ui.xaml.dll",
        "Microsoft.UI.Xaml.Controls.dll",
        "Microsoft.UI.Input.dll",
        "Microsoft.UI.Windowing.dll",
        "Microsoft.Internal.FrameworkUdk.dll"
    )

    foreach ($dll in $loadOrder) {
        $dllPath = Join-Path $ChronosPath $dll
        if (Test-Path $dllPath) {
            $r = [DllLoadTest]::TryLoad($dllPath)
            $passed = $r -eq "OK"
            Write-Check $dll $passed $r
        } else {
            Write-Check $dll $false "NOT FOUND"
        }
    }
} catch {
    Write-Check "DLL load test" $false $_.Exception.Message
}

Write-Host ""

# ===================================================================
# Section 6: API Set Resolution
# ===================================================================
Write-Host "--- API Set Resolution ---" -ForegroundColor Yellow
Write-Host ""

# These API sets are imported by WinAppSDK DLLs
$apiSets = @(
    "api-ms-win-core-winrt-l1-1-0.dll",
    "api-ms-win-core-winrt-string-l1-1-0.dll",
    "api-ms-win-core-winrt-registration-l1-1-0.dll",
    "api-ms-win-core-winrt-roparameterizediid-l1-1-0.dll",
    "api-ms-win-core-com-l1-1-0.dll",
    "api-ms-win-core-com-l1-1-1.dll",
    "api-ms-win-appmodel-runtime-l1-1-2.dll",
    "api-ms-win-appmodel-runtime-l1-1-3.dll",
    "ext-ms-win-uiacore-l1-1-0.dll"
)

try {
    foreach ($api in $apiSets) {
        $p = "$env:SystemRoot\System32\$api"
        if (Test-Path $p) {
            Write-Check $api $true "present in System32"
        } else {
            # Try to load it - API sets resolve even without files
            $r = [DllLoadTest]::TryLoad($api)
            if ($r -eq "OK") {
                Write-Check $api $true "resolves via API set (no file)"
            } else {
                Write-Check $api $false "does NOT resolve: $r"
            }
        }
    }
} catch {
    Write-Check "API set test" $false $_.Exception.Message
}

Write-Host ""

# ===================================================================
# Section 7: Process Environment Check
# ===================================================================
Write-Host "--- Process Environment ---" -ForegroundColor Yellow
Write-Host ""

# Check PATH for the Chronos directory
$pathDirs = $env:PATH -split ";"
$chronosInPath = $false
foreach ($d in $pathDirs) {
    if ($d -and (Test-Path $d -ErrorAction SilentlyContinue)) {
        $resolved = (Resolve-Path $d -ErrorAction SilentlyContinue).Path
        if ($resolved -eq $ChronosPath) {
            $chronosInPath = $true
            break
        }
    }
}
Write-Check "Chronos dir in PATH" $chronosInPath
if (-not $chronosInPath) {
    Write-Info "Recommendation" "Add Chronos dir to PATH before launch"
}

# Check if running as admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Check "Running as Administrator" $isAdmin

# Check PE detection
$isWinPE = $false
try {
    $miniNT = Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\MiniNT" -ErrorAction SilentlyContinue
    if ($miniNT) { $isWinPE = $true }
} catch {}
Write-Check "WinPE environment detected" $isWinPE

# Check available RAM
$totalMem = 0
try {
    $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
    if ($cs) {
        $totalMem = [math]::Round($cs.TotalPhysicalMemory / 1MB, 0)
        Write-Info "Total RAM" "$totalMem MB"
    }
} catch {
    Write-Warn "Could not query RAM"
}

Write-Host ""

# ===================================================================
# Section 8: Bootstrap Initialization Test
# ===================================================================
Write-Host "--- WinAppSDK Bootstrap Test ---" -ForegroundColor Yellow
Write-Host ""

$bootstrapDll = Join-Path $ChronosPath "Microsoft.WindowsAppRuntime.Bootstrap.dll"
$bootstrapNetDll = Join-Path $ChronosPath "Microsoft.WindowsAppRuntime.Bootstrap.Net.dll"

Write-Check "Bootstrap.dll present" (Test-Path $bootstrapDll)
Write-Check "Bootstrap.Net.dll present" (Test-Path $bootstrapNetDll)

# Check if Bootstrap.dll has the expected exports
if (Test-Path $bootstrapDll) {
    try {
        $r = [DllLoadTest]::TryLoad($bootstrapDll)
        Write-Check "Bootstrap.dll loadable" ($r -eq "OK") $r
    } catch {
        Write-Check "Bootstrap.dll loadable" $false $_.Exception.Message
    }
}

# Check for MddBootstrapInitialize export
try {
    $bootstrapExportCode = @"
using System;
using System.Runtime.InteropServices;

public class BootstrapCheck {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    public static string CheckExports(string dllPath) {
        IntPtr h = LoadLibraryW(dllPath);
        if (h == IntPtr.Zero) return "FAIL: cannot load";

        string[] exports = new string[] {
            "MddBootstrapInitialize",
            "MddBootstrapInitialize2",
            "MddBootstrapShutdown"
        };

        string result = "";
        foreach (string exp in exports) {
            IntPtr proc = GetProcAddress(h, exp);
            result += exp + "=" + (proc != IntPtr.Zero ? "YES" : "NO") + " ";
        }

        FreeLibrary(h);
        return result;
    }
}
"@
    Add-Type -TypeDefinition $bootstrapExportCode -ErrorAction Stop

    if (Test-Path $bootstrapDll) {
        $r = [BootstrapCheck]::CheckExports($bootstrapDll)
        Write-Info "Bootstrap exports" $r
    }
} catch {
    Write-Warn "Bootstrap export check failed" $_.Exception.Message
}

Write-Host ""

# ===================================================================
# Section 9: Recommendations
# ===================================================================
Write-Host "--- Recommendations ---" -ForegroundColor Yellow
Write-Host ""

# Gather DWM status
$hasDwm = (Get-Process -Name "dwm" -ErrorAction SilentlyContinue) -ne $null
$hasDwmExe = Test-Path "$env:SystemRoot\System32\dwm.exe"
$hasCombase = Test-Path "$env:SystemRoot\System32\combase.dll"

if (-not $hasDwm) {
    Write-Host "  CRITICAL: DWM is not running." -ForegroundColor Red
    Write-Host ""
    if ($hasDwmExe) {
        Write-Host "  dwm.exe exists. Try starting it:" -ForegroundColor White
        Write-Host '    net start UxSms' -ForegroundColor Gray
        Write-Host '    -- or --' -ForegroundColor DarkGray
        Write-Host '    start dwm.exe' -ForegroundColor Gray
    } else {
        Write-Host "  dwm.exe is NOT present in this PE image." -ForegroundColor White
        Write-Host "  You MUST add DWM support in your PhoenixPE build." -ForegroundColor White
        Write-Host "  In PhoenixPE, enable:" -ForegroundColor White
        Write-Host "    - Desktop Window Manager (DWM) plugin" -ForegroundColor Gray
        Write-Host "    - DirectX support" -ForegroundColor Gray
        Write-Host "    - Explorer shell (often starts DWM automatically)" -ForegroundColor Gray
    }
    Write-Host ""
    Write-Host "  WinUI 3 absolutely requires DWM for rendering." -ForegroundColor Yellow
    Write-Host "  Without DWM, even if WinRT activation succeeds," -ForegroundColor Yellow
    Write-Host "  the app cannot display any windows." -ForegroundColor Yellow
    Write-Host ""
}

if (-not $hasCombase) {
    Write-Host "  CRITICAL: combase.dll is missing." -ForegroundColor Red
    Write-Host "  WinRT activation requires combase.dll (RoGetActivationFactory)." -ForegroundColor White
    Write-Host ""
}

Write-Host "  If DWM is running but activation still fails:" -ForegroundColor White
Write-Host "    The v0.3.0+ build includes automatic WinAppSDK runtime" -ForegroundColor Gray
Write-Host "    initialization (UndockedRegFreeWinRT) in Program.cs." -ForegroundColor Gray
Write-Host "    This should resolve the 0x80040111 error." -ForegroundColor Gray
Write-Host "" 
Write-Host "    If it still fails, try:" -ForegroundColor White
Write-Host "    1. Launch from the Chronos directory:" -ForegroundColor Gray
Write-Host "       cd X:\Path\To\Chronos" -ForegroundColor Gray
Write-Host "       .\Chronos.App.exe" -ForegroundColor Gray
Write-Host "    2. Set base directory manually before launching:" -ForegroundColor Gray
Write-Host '       $env:MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY = "X:\Path\To\Chronos\"' -ForegroundColor Gray
Write-Host "       .\Chronos.App.exe" -ForegroundColor Gray
Write-Host ""

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  Diagnostic complete." -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
