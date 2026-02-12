using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Chronos.Common.Helpers;

/// <summary>
/// Detects whether the application is running in a Windows PE (Preinstallation Environment)
/// and provides information about what OS capabilities are available.
/// </summary>
public static class PeEnvironment
{
    private static bool? _isWinPE;
    private static PeCapabilities? _capabilities;

    /// <summary>
    /// Returns true if the application is running inside Windows PE.
    /// </summary>
    public static bool IsWinPE
    {
        get
        {
            _isWinPE ??= DetectWinPE();
            return _isWinPE.Value;
        }
    }

    /// <summary>
    /// Returns the detected PE capabilities (lazily evaluated and cached).
    /// </summary>
    public static PeCapabilities Capabilities
    {
        get
        {
            _capabilities ??= ProbeCapabilities();
            return _capabilities;
        }
    }

    /// <summary>
    /// Gets the application data directory, using a fallback location in PE environments
    /// where %LOCALAPPDATA% may be empty or point to a RAM disk.
    /// </summary>
    public static string GetAppDataDirectory()
    {
        // Try standard LocalAppData first
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrEmpty(localAppData))
        {
            try
            {
                var dir = Path.Combine(localAppData, "Chronos");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                // Fall through to fallbacks
            }
        }

        // Fallback 1: Next to the executable
        var exeDir = AppContext.BaseDirectory;
        var portableDir = Path.Combine(exeDir, "AppData");
        try
        {
            Directory.CreateDirectory(portableDir);
            return portableDir;
        }
        catch
        {
            // Fall through
        }

        // Fallback 2: X:\Chronos (common WinPE RAM drive)
        if (Directory.Exists(@"X:\"))
        {
            var xDir = @"X:\Chronos";
            try
            {
                Directory.CreateDirectory(xDir);
                return xDir;
            }
            catch { }
        }

        // Fallback 3: Temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), "Chronos");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    /// <summary>
    /// Detects if we are running inside WinPE via multiple heuristics.
    /// </summary>
    private static bool DetectWinPE()
    {
        // Method 1: Registry key (most reliable)
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WinPE");
            if (key is not null)
                return true;
        }
        catch { }

        // Method 2: MiniNT registry key (set by WinPE boot)
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT");
            if (key is not null)
                return true;
        }
        catch { }

        // Method 3: GetSystemMetrics(SM_CLEANBOOT) — returns 1 in safe mode, but WinPE
        // also lacks certain characteristics
        // Method 4: Check for startnet.cmd (WinPE initialization script)
        try
        {
            var sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"X:\Windows";
            if (File.Exists(Path.Combine(sysRoot, "System32", "startnet.cmd")))
                return true;
        }
        catch { }

        // Method 5: Check for winpeshl.exe (WinPE shell launcher)
        try
        {
            var sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"X:\Windows";
            if (File.Exists(Path.Combine(sysRoot, "System32", "winpeshl.exe")))
                return true;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Probes the current environment for available OS capabilities.
    /// </summary>
    private static PeCapabilities ProbeCapabilities()
    {
        var caps = new PeCapabilities();

        // Check WMI
        caps.HasWmi = ProbeWmi();

        // Check VSS
        caps.HasVss = ProbeService("VSS") && ProbeFile("vssapi.dll");

        // Check DWM
        caps.HasDwm = ProbeService("uxsms");

        // Check virtdisk.dll
        caps.HasVirtualDiskApi = ProbeFile("virtdisk.dll");

        // Check network
        caps.HasNetwork = ProbeNetwork();

        // Check LocalAppData
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        caps.HasPersistentStorage = !string.IsNullOrEmpty(localAppData);

        // Check COM file dialogs
        caps.HasShellDialogs = ProbeFile("comdlg32.dll");

        return caps;
    }

    private static bool ProbeWmi()
    {
        try
        {
            return ProbeService("Winmgmt");
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeService(string serviceName)
    {
        try
        {
            // Use SC query via registry to avoid System.ServiceProcess dependency
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeFile(string systemDll)
    {
        try
        {
            var sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            return File.Exists(Path.Combine(sysRoot, "System32", systemDll));
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeNetwork()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Describes what OS-level capabilities are available in the current environment.
/// </summary>
public class PeCapabilities
{
    /// <summary>WMI service (Winmgmt) is available for disk enumeration.</summary>
    public bool HasWmi { get; set; }

    /// <summary>Volume Shadow Copy Service is available for live snapshots.</summary>
    public bool HasVss { get; set; }

    /// <summary>Desktop Window Manager is running (required for WinUI 3).</summary>
    public bool HasDwm { get; set; }

    /// <summary>Virtual Disk API (virtdisk.dll) is available for VHDX operations.</summary>
    public bool HasVirtualDiskApi { get; set; }

    /// <summary>Network stack is initialized and connected.</summary>
    public bool HasNetwork { get; set; }

    /// <summary>LocalAppData directory resolves to a writable path.</summary>
    public bool HasPersistentStorage { get; set; }

    /// <summary>Shell file dialogs (Open/Save) are available.</summary>
    public bool HasShellDialogs { get; set; }

    public override string ToString()
    {
        return $"WMI={HasWmi}, VSS={HasVss}, DWM={HasDwm}, VirtDisk={HasVirtualDiskApi}, " +
               $"Network={HasNetwork}, Storage={HasPersistentStorage}, ShellDialogs={HasShellDialogs}";
    }
}
