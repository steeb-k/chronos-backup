using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Chronos.Common.Helpers;
using Chronos.Core.Models;
using Chronos.Core.Services;
using Chronos.Core.VSS;
using Chronos.Native.Win32;

namespace Chronos.App.Diagnostics;

/// <summary>
/// Headless self-test that exercises every subsystem Chronos depends on.
/// Designed to run in WinPE (or full Windows) via: Chronos.exe --selftest
/// Writes results to both stdout and chronos-selftest.log.
/// Exit code = number of failed tests (0 = all passed).
/// </summary>
public static class SelfTest
{
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "chronos-selftest.log");

    private static int _pass, _fail, _skip;

    public static async Task<int> RunAsync()
    {
        // Clear previous log
        try { File.Delete(LogPath); } catch { }

        Log("╔══════════════════════════════════════════╗");
        Log("║       Chronos Self-Test Diagnostics      ║");
        Log("╚══════════════════════════════════════════╝");
        Log("");
        Log($"Time:           {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"Machine:        {Environment.MachineName}");
        Log($"OS:             {Environment.OSVersion}");
        Log($"64-bit OS:      {Environment.Is64BitOperatingSystem}");
        Log($"64-bit Process: {Environment.Is64BitProcess}");
        Log($"Architecture:   {RuntimeInformation.ProcessArchitecture}");
        Log($"BaseDirectory:  {AppContext.BaseDirectory}");
        Log($".NET Version:   {RuntimeInformation.FrameworkDescription}");
        Log("");

        // ── Section 1: Environment Detection ──────────────────────
        Section("ENVIRONMENT DETECTION");

        Test("PeEnvironment.IsWinPE", () =>
        {
            bool isWinPE = PeEnvironment.IsWinPE;
            Log($"  IsWinPE = {isWinPE}");
            return true; // informational — pass either way
        });

        Test("PeEnvironment.Capabilities", () =>
        {
            var caps = PeEnvironment.Capabilities;
            Log($"  HasWmi:              {caps.HasWmi}");
            Log($"  HasVss:              {caps.HasVss}");
            Log($"  HasDwm:              {caps.HasDwm}");
            Log($"  HasVirtualDiskApi:   {caps.HasVirtualDiskApi}");
            Log($"  HasNetwork:          {caps.HasNetwork}");
            Log($"  HasPersistentStorage:{caps.HasPersistentStorage}");
            Log($"  HasShellDialogs:     {caps.HasShellDialogs}");
            return true;
        });

        Test("PeEnvironment.GetAppDataDirectory", () =>
        {
            var dir = PeEnvironment.GetAppDataDirectory();
            Log($"  AppData directory: {dir}");
            Log($"  Exists: {Directory.Exists(dir)}");
            return !string.IsNullOrEmpty(dir);
        });

        // ── Section 2: Disk Enumeration ───────────────────────────
        Section("DISK ENUMERATION");

        Test("DiskApi.ProbePhysicalDiskIndices (IOCTL)", () =>
        {
            var indices = DiskApi.ProbePhysicalDiskIndices();
            Log($"  Physical disks found: {indices.Count}");
            foreach (var idx in indices)
                Log($"    PhysicalDrive{idx}");
            return indices.Count > 0;
        });

        Test("DiskApi.GetDiskGeometry (IOCTL)", () =>
        {
            var indices = DiskApi.ProbePhysicalDiskIndices();
            bool anyOk = false;
            foreach (var idx in indices)
            {
                var geo = DiskApi.GetDiskGeometry(idx);
                if (geo.HasValue)
                {
                    Log($"  Disk {idx}: Size={geo.Value.DiskSize / (1024 * 1024 * 1024)} GB, " +
                        $"BytesPerSector={geo.Value.BytesPerSector}, MediaType={geo.Value.MediaType}");
                    anyOk = true;
                }
                else
                {
                    Log($"  Disk {idx}: GetDiskGeometry FAILED");
                }
            }
            return anyOk;
        });

        Test("DiskApi.GetDiskModelViaIoctl (StorageDeviceProperty)", () =>
        {
            var indices = DiskApi.ProbePhysicalDiskIndices();
            bool anyOk = false;
            foreach (var idx in indices)
            {
                var model = DiskApi.GetDiskModelViaIoctl(idx);
                Log($"  Disk {idx}: Model = {model ?? "(null)"}");
                if (model != null) anyOk = true;
            }
            return anyOk;
        });

        await TestAsync("DiskEnumerator.GetDisksAsync (full pipeline)", async () =>
        {
            var enumerator = new DiskEnumerator();
            var disks = await enumerator.GetDisksAsync();
            Log($"  Disks enumerated: {disks.Count}");
            foreach (var d in disks)
            {
                Log($"    Disk {d.DiskNumber}: {d.Model} " +
                    $"({d.Size / (1024UL * 1024 * 1024)} GB) " +
                    $"Style={d.PartitionStyle} System={d.IsSystemDisk} Boot={d.IsBootDisk}");
            }
            return disks.Count > 0;
        });

        await TestAsync("DiskEnumerator.GetPartitionsAsync", async () =>
        {
            var enumerator = new DiskEnumerator();
            var disks = await enumerator.GetDisksAsync();
            bool anyParts = false;
            foreach (var d in disks)
            {
                var parts = await enumerator.GetPartitionsAsync(d.DiskNumber);
                Log($"  Disk {d.DiskNumber}: {parts.Count} partition(s)");
                foreach (var p in parts)
                {
                    Log($"    Part {p.PartitionNumber}: {p.DisplayLabel} " +
                        $"FS={p.FileSystem ?? "(null)"} Size={p.Size / (1024UL * 1024)} MB");
                }
                if (parts.Count > 0) anyParts = true;
            }
            return anyParts;
        });

        // ── Section 3: Volume APIs ────────────────────────────────
        Section("VOLUME APIS");

        Test("VolumeApi.EnumerateVolumeGuids", () =>
        {
            var guids = VolumeApi.EnumerateVolumeGuids();
            Log($"  Volume GUIDs found: {guids.Count}");
            foreach (var g in guids)
                Log($"    {g}");
            return guids.Count > 0;
        });

        Test("VolumeApi.GetVolumeInformation (per drive letter)", () =>
        {
            var drives = DriveInfo.GetDrives();
            int successCount = 0;
            foreach (var d in drives)
            {
                var (fs, label) = VolumeApi.GetVolumeInformation(d.Name);
                Log($"  {d.Name} -> FS={fs ?? "(null)"}, Label={label ?? "(null)"}");
                if (fs != null) successCount++;
            }
            Log($"  Successfully queried: {successCount}/{drives.Length}");
            return successCount > 0;
        });

        Test("DriveInfo enumeration", () =>
        {
            var drives = DriveInfo.GetDrives();
            Log($"  Drives found: {drives.Length}");
            foreach (var d in drives)
            {
                try
                {
                    Log($"  {d.Name} Type={d.DriveType} Ready={d.IsReady}" +
                        (d.IsReady ? $" FS={d.DriveFormat} Label=\"{d.VolumeLabel}\" " +
                                     $"Free={d.AvailableFreeSpace / (1024 * 1024)} MB"
                                  : ""));
                }
                catch (Exception ex) { Log($"  {d.Name} Type={d.DriveType} ERROR: {ex.Message}"); }
            }
            return drives.Length > 0;
        });

        Test("Target drive detection (Fixed + Removable)", () =>
        {
            var targets = DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .ToList();
            Log($"  Potential target drives: {targets.Count}");
            foreach (var t in targets)
            {
                try
                {
                    Log($"    {t.Name} Type={t.DriveType} FS={t.DriveFormat} " +
                        $"Total={t.TotalSize / (1024 * 1024)} MB Free={t.AvailableFreeSpace / (1024 * 1024)} MB");
                }
                catch (Exception ex) { Log($"    {t.Name} ERROR: {ex.Message}"); }
            }
            return targets.Count > 0;
        });

        // ── Section 4: VSS ────────────────────────────────────────
        Section("VOLUME SHADOW COPY SERVICE");

        Test("VssService.IsVssAvailable", () =>
        {
            var vss = new VssService();
            bool available = vss.IsVssAvailable();
            Log($"  VSS available: {available}");
            if (!available)
            {
                Log($"  Reason: {vss.LastAvailabilityError ?? "unknown"}");
                if (PeEnvironment.IsWinPE)
                    Log($"  (VSS may require the service to be started: 'net start VSS')");
            }
            return true; // informational
        });

        Test("VSS service state (registry)", () =>
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\VSS");
                if (key == null)
                {
                    Log("  VSS service: NOT REGISTERED (no registry key)");
                    return true;
                }
                var start = key.GetValue("Start");
                var type = key.GetValue("Type");
                var imagePath = key.GetValue("ImagePath");
                Log($"  VSS service Start={start} Type={type}");
                Log($"  ImagePath: {imagePath}");

                // Check if the binary actually exists
                var expanded = Environment.ExpandEnvironmentVariables(imagePath?.ToString() ?? "");
                // Strip service flags like -k netsvcs
                var exePath = expanded.Split(' ').FirstOrDefault(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                           ?? expanded.Split(' ').FirstOrDefault() ?? expanded;
                Log($"  Binary exists: {File.Exists(exePath)} ({exePath})");
            }
            catch (Exception ex)
            {
                Log($"  Error querying VSS service: {ex.Message}");
            }
            return true;
        });

        Test("vssapi.dll presence", () =>
        {
            string sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            string sys32 = Path.Combine(sysRoot, "System32");
            var vssapiPath = Path.Combine(sys32, "vssapi.dll");
            var vssApiPath = Path.Combine(sys32, "VssApi.dll"); // alternate casing
            bool exists = File.Exists(vssapiPath); // case-insensitive on Windows
            Log($"  vssapi.dll in System32: {exists}");
            if (exists)
            {
                var fi = new FileInfo(vssapiPath);
                Log($"  Size: {fi.Length} bytes, Modified: {fi.LastWriteTime}");
            }
            return exists || !PeEnvironment.IsWinPE; // only fail in PE if missing
        });

        // ── Section 5: WMI ────────────────────────────────────────
        Section("WMI (Windows Management Instrumentation)");

        if (PeEnvironment.Capabilities.HasWmi)
        {
            TestWmi("Win32_DiskDrive", "SELECT Index, Model, Size FROM Win32_DiskDrive",
                obj => $"    Disk {obj["Index"]}: {obj["Model"]} ({(ulong)(obj["Size"] ?? 0UL) / (1024UL * 1024 * 1024)} GB)");

            TestWmi("Win32_DiskPartition", "SELECT DiskIndex, Index, Size, Type FROM Win32_DiskPartition",
                obj => $"    Disk{obj["DiskIndex"]} Part{obj["Index"]}: {obj["Type"]} ({(ulong)(obj["Size"] ?? 0UL) / (1024UL * 1024)} MB)");

            TestWmi("Win32_Volume", "SELECT DriveLetter, FileSystem, Label, Capacity FROM Win32_Volume",
                obj => $"    {obj["DriveLetter"] ?? "(no letter)"}: FS={obj["FileSystem"]} Label=\"{obj["Label"]}\"");

            TestWmi("Win32_LogicalDisk", "SELECT DeviceID, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk",
                obj => $"    {obj["DeviceID"]}: FS={obj["FileSystem"]} Free={(ulong)(obj["FreeSpace"] ?? 0UL) / (1024UL * 1024)} MB");

            TestWmi("Win32_Service (VSS)", "SELECT Name, State FROM Win32_Service WHERE Name='VSS'",
                obj => $"    VSS service state: {obj["State"]}");

            TestWmi("Win32_ShadowCopy", "SELECT ID, VolumeName FROM Win32_ShadowCopy",
                obj => $"    Shadow: {obj["ID"]} on {obj["VolumeName"]}");

            TestWmi("Win32_ComputerSystem", "SELECT Name, TotalPhysicalMemory FROM Win32_ComputerSystem",
                obj => $"    {obj["Name"]}: RAM={(ulong)(obj["TotalPhysicalMemory"] ?? 0UL) / (1024UL * 1024)} MB");
        }
        else
        {
            Log("[SKIP] WMI tests — Capabilities.HasWmi = false");
            Log("  (Disk enumeration will use IOCTL fallback path)");
            _skip += 7;
        }

        // ── Section 6: File I/O ───────────────────────────────────
        Section("FILE I/O");

        Test("Write to BaseDirectory", () =>
        {
            var testFile = Path.Combine(AppContext.BaseDirectory, "selftest-probe.tmp");
            File.WriteAllText(testFile, "Chronos self-test write probe");
            bool exists = File.Exists(testFile);
            if (exists) File.Delete(testFile);
            Log($"  Write to BaseDirectory: {(exists ? "OK" : "FAILED")}");
            return exists;
        });

        Test("Write to AppData", () =>
        {
            var appData = PeEnvironment.GetAppDataDirectory();
            var testFile = Path.Combine(appData, "selftest-probe.tmp");
            File.WriteAllText(testFile, "Chronos self-test write probe");
            bool exists = File.Exists(testFile);
            if (exists) File.Delete(testFile);
            Log($"  Write to AppData ({appData}): {(exists ? "OK" : "FAILED")}");
            return exists;
        });

        // ── Section 7: DLL Dependencies ───────────────────────────
        Section("CRITICAL DLL DEPENDENCIES");

        string sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        string sys32 = Path.Combine(sysRoot, "System32");

        string[] criticalDlls =
        [
            "combase.dll", "ole32.dll", "oleaut32.dll",
            "comdlg32.dll", "dwmapi.dll", "dxgi.dll",
            "d3d11.dll", "d2d1.dll", "dwrite.dll",
            "windowscodecs.dll",   // WIC — expected broken in WinPE
            "vssapi.dll",          // VSS — expected missing in WinPE
            "virtdisk.dll",        // Virtual disk API
        ];

        foreach (var dll in criticalDlls)
        {
            Test($"DLL: {dll}", () =>
            {
                bool exists = File.Exists(Path.Combine(sys32, dll));
                Log($"  {dll}: {(exists ? "PRESENT" : "MISSING")}");
                return exists;
            });
        }

        // WinAppSDK DLLs (in app directory)
        string[] appSdkDlls =
        [
            "Microsoft.WindowsAppRuntime.dll",
            "Microsoft.ui.xaml.dll",
            "Microsoft.WindowsAppRuntime.Bootstrap.dll",
            "MRM.dll",
        ];

        foreach (var dll in appSdkDlls)
        {
            Test($"AppSDK: {dll}", () =>
            {
                bool exists = File.Exists(Path.Combine(AppContext.BaseDirectory, dll));
                Log($"  {dll}: {(exists ? "PRESENT" : "MISSING")}");
                return exists;
            });
        }

        // ── Summary ──────────────────────────────────────────────
        Log("");
        Log("╔══════════════════════════════════════════╗");
        Log($"║  PASSED: {_pass,-5} FAILED: {_fail,-5} SKIPPED: {_skip,-5}║");
        Log("╚══════════════════════════════════════════╝");
        Log("");

        if (_fail == 0)
            Log("✓ ALL TESTS PASSED");
        else
            Log($"✗ {_fail} TEST(S) FAILED — review details above");

        Log("");
        Log($"Log saved to: {LogPath}");

        return _fail;
    }

    // ── Test harness methods ──────────────────────────────────────

    private static void Test(string name, Func<bool> action)
    {
        try
        {
            bool result = action();
            if (result) { _pass++; Log($"  [PASS] {name}"); }
            else        { _fail++; Log($"  [FAIL] {name}"); }
        }
        catch (Exception ex)
        {
            _fail++;
            Log($"  [FAIL] {name}");
            Log($"         {ex.GetType().Name}: {ex.Message}");
            var firstFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
            if (firstFrame != null) Log($"         {firstFrame}");
        }
    }

    private static async Task TestAsync(string name, Func<Task<bool>> action)
    {
        try
        {
            bool result = await action();
            if (result) { _pass++; Log($"  [PASS] {name}"); }
            else        { _fail++; Log($"  [FAIL] {name}"); }
        }
        catch (Exception ex)
        {
            _fail++;
            Log($"  [FAIL] {name}");
            Log($"         {ex.GetType().Name}: {ex.Message}");
            var firstFrame = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
            if (firstFrame != null) Log($"         {firstFrame}");
        }
    }

    /// <summary>
    /// WMI test isolated behind NoInlining to prevent JIT from loading
    /// System.Management.dll when WMI is unavailable.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestWmi(string label, string query, Func<System.Management.ManagementBaseObject, string> formatter)
    {
        Test($"WMI: {label}", () =>
        {
            using var searcher = new System.Management.ManagementObjectSearcher(query);
            var results = searcher.Get();
            int count = 0;
            foreach (System.Management.ManagementObject obj in results)
            {
                Log(formatter(obj));
                count++;
                obj.Dispose();
            }
            Log($"  {label}: {count} result(s)");
            return true; // WMI query succeeded
        });
    }

    private static void Section(string title)
    {
        Log("");
        Log($"── {title} {"".PadRight(40 - title.Length, '─')}");
        Log("");
    }

    private static void Log(string msg)
    {
        Console.WriteLine(msg);
        try { File.AppendAllText(LogPath, msg + Environment.NewLine); }
        catch { /* best effort */ }
    }
}
