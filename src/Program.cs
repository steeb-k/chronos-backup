using System.Runtime.InteropServices;
using System.Text;

namespace Chronos.App;

/// <summary>
/// Custom entry point with comprehensive startup diagnostics for WinPE.
/// Includes a PE import dependency walker that identifies exactly which
/// transitive dependency fails to load and causes error 126.
/// Results are written to chronos-startup.log in the app directory.
/// </summary>
public static class Program
{
    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);
    private const uint ATTACH_PARENT_PROCESS = unchecked((uint)-1);

    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(
        uint first, IntPtr handler);

    [DllImport("kernel32.dll")]
    private static extern uint RemoveVectoredExceptionHandler(IntPtr handle);

    private delegate IntPtr SetUnhandledExceptionFilterDelegate(
        IntPtr lpTopLevelExceptionFilter);

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetUnhandledExceptionFilter(
        IntPtr lpTopLevelExceptionFilter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TopLevelExceptionFilterDelegate(IntPtr pExceptionInfo);

    private static TopLevelExceptionFilterDelegate? _topLevelFilterDelegate;

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern unsafe int RoGetActivationFactory(
        IntPtr activatableClassId, Guid* iid, IntPtr* factory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumProcessModules(
        IntPtr hProcess, IntPtr[] lphModule, uint cb,
        out uint lpcbNeeded);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameExW(
        IntPtr hProcess, IntPtr hModule, StringBuilder lpFilename,
        uint nSize);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleInformation(
        IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo,
        uint cb);

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOOPENFILEERRORBOX = 0x8000;
    private const int EXCEPTION_CONTINUE_SEARCH = 0;

    private static readonly StringBuilder _log = new();
    private static int _flushedLength;

    internal static void Log(string msg)
    {
        _log.AppendLine(msg);
    }

    internal static void FlushLog()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "chronos-startup.log");
            var content = _log.ToString();
            if (_flushedLength == 0)
            {
                File.WriteAllText(path, content);
            }
            else
            {
                // Append only new content since last flush
                File.AppendAllText(path, content.Substring(_flushedLength));
            }
            _flushedLength = content.Length;
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Probes WMI capabilities. Isolated in NoInlining method to prevent JIT from
    /// loading System.Management.dll if this method is never called.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ProbeWmiCapabilities()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_ComputerSystem");
            var results = searcher.Get();
            int count = 0;
            foreach (var r in results) { count++; r.Dispose(); }
            Log("  WMI: Available (Win32_ComputerSystem returned " + count + " result(s))");
        }
        catch (Exception ex)
        {
            Log("  WMI: Not available (" + ex.GetType().Name + ": " + ex.Message + ")");
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Headless self-test mode: exercises all subsystems without launching the GUI.
        // Usage: Chronos.exe --selftest
        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            // Attach a console so output is visible when launched from cmd/PowerShell
            AttachConsole(ATTACH_PARENT_PROCESS);
            Console.WriteLine(); // blank line after the shell prompt
            var exitCode = Chronos.App.Diagnostics.SelfTest.RunAsync().GetAwaiter().GetResult();
            Environment.Exit(exitCode);
            return;
        }

        var baseDir = AppContext.BaseDirectory;

        // Catch ALL unhandled exceptions including native crashes
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log("");
            Log("=== UNHANDLED EXCEPTION ===");
            if (e.ExceptionObject is Exception ex)
            {
                Log("Type: " + ex.GetType().FullName);
                Log("HResult: 0x" + ex.HResult.ToString("X8"));
                Log("Message: " + ex.Message);
                Log("Stack: " + ex.StackTrace);
                if (ex.InnerException != null)
                {
                    Log("Inner: " + ex.InnerException.GetType().FullName);
                    Log("InnerMsg: " + ex.InnerException.Message);
                    Log("InnerHR: 0x" + ex.InnerException.HResult.ToString("X8"));
                }
            }
            else
            {
                Log("ExceptionObject: " + (e.ExceptionObject?.ToString() ?? "null"));
            }
            Log("IsTerminating: " + e.IsTerminating);
            FlushLog();
        };

        // Install native vectored exception handler to catch crashes
        // that bypass managed exception handling
        _vehDelegate = new VectoredExceptionDelegate(VectoredExceptionHandler);
        _vehHandle = AddVectoredExceptionHandler(1,
            Marshal.GetFunctionPointerForDelegate(_vehDelegate));

        // Install top-level exception filter to catch native FailFast/crashes
        // that bypass ProcessExit and managed exception handlers
        _topLevelFilterDelegate = TopLevelExceptionFilter;
        SetUnhandledExceptionFilter(
            Marshal.GetFunctionPointerForDelegate(_topLevelFilterDelegate));

        // Suppress system error dialogs for missing DLLs
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);

        Log("=== Chronos Startup Diagnostics ===");
        Log("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Log("BaseDirectory: " + baseDir);
        Log("OS: " + Environment.OSVersion);
        Log("64-bit process: " + Environment.Is64BitProcess);
        Log("PeEnvironment.IsWinPE: " + Chronos.Common.Helpers.PeEnvironment.IsWinPE);

        // Set up DLL resolution paths
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", baseDir);
        try { Directory.SetCurrentDirectory(baseDir); } catch { }
        SetDllDirectoryW(baseDir);

        // Load WinAppSDK runtime
        try
        {
            int hr = WindowsAppRuntime_EnsureIsLoaded();
            Log("WindowsAppRuntime_EnsureIsLoaded: 0x" + hr.ToString("X8") +
                (hr >= 0 ? " (OK)" : " (FAILED)"));
        }
        catch (Exception ex)
        {
            Log("WindowsAppRuntime_EnsureIsLoaded EXCEPTION: " + ex.GetType().Name +
                ": " + ex.Message);
        }

        // DLL load preflight with full dependency walking
        Log("");
        Log("=== DLL Load Pre-flight ===");

        string[] criticalDlls = new[]
        {
            "Microsoft.Internal.FrameworkUdk.dll",
            "CoreMessagingXP.dll",
            "Microsoft.UI.Windowing.Core.dll",
            "Microsoft.ui.xaml.dll",
            "Microsoft.UI.Xaml.Controls.dll",
            "Microsoft.UI.Xaml.Internal.dll",
            "Microsoft.UI.dll",
            "Microsoft.UI.Composition.OSSupport.dll",
            "Microsoft.UI.Input.dll",
            "Microsoft.ui.xaml.phone.dll",
        };

        foreach (var dllName in criticalDlls)
        {
            var dllPath = Path.Combine(baseDir, dllName);
            if (!File.Exists(dllPath))
            {
                Log("  " + dllName + ": FILE NOT FOUND");
                continue;
            }

            var handle = LoadLibraryExW(dllPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (handle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Log("  " + dllName + ": LoadLibrary FAILED (error " + err + ")");

                // Walk imports to find the specific missing dependency
                Log("    --- Import dependency walk ---");
                WalkImports(dllPath, baseDir, "    ");
            }
            else
            {
                var proc = GetProcAddress(handle, "DllGetActivationFactory");
                Log("  " + dllName + ": OK" +
                    (proc != IntPtr.Zero ? " (has DllGetActivationFactory)" : ""));
            }
        }

        // Check system DLL availability
        Log("");
        Log("=== System DLL Availability ===");
        string[] sysDlls = new[]
        {
            "combase.dll", "user32.dll", "gdi32.dll", "ole32.dll",
            "oleaut32.dll", "advapi32.dll", "version.dll", "shlwapi.dll",
            "dwmapi.dll", "dcomp.dll", "d2d1.dll", "d3d11.dll",
            "dwrite.dll", "dxgi.dll", "kernel.appcore.dll", "WinTypes.dll",
            "shcore.dll", "uxtheme.dll", "coremessaging.dll",
            "InputHost.dll", "UIAutomationCore.dll", "propsys.dll",
            "WindowsCodecs.dll", "urlmon.dll",
        };

        var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var name in sysDlls)
        {
            bool inApp = File.Exists(Path.Combine(baseDir, name));
            bool inSys = File.Exists(Path.Combine(sysDir, name));
            string status = inApp ? "bundled" : (inSys ? "system" : "MISSING");
            Log("  " + name + ": " + status);
        }

        // DWM check
        Log("");
        try
        {
            var dwm = System.Diagnostics.Process.GetProcessesByName("dwm");
            Log("DWM processes: " + dwm.Length);
            foreach (var p in dwm) { p.Dispose(); }
        }
        catch { }

        FlushLog();

        // Test DllGetActivationFactory manually before CsWinRT tries
        Log("");
        Log("=== Manual Activation Factory Test ===");
        try
        {
            var xamlDll = Path.Combine(baseDir, "Microsoft.ui.xaml.dll");
            var hMod = LoadLibraryExW(xamlDll, IntPtr.Zero,
                LOAD_WITH_ALTERED_SEARCH_PATH);
            if (hMod != IntPtr.Zero)
            {
                var proc = GetProcAddress(hMod, "DllGetActivationFactory");
                if (proc != IntPtr.Zero)
                {
                    TestActivationFactory(proc,
                        "Microsoft.UI.Xaml.Application");
                    TestActivationFactory(proc,
                        "Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider");
                }
                else
                {
                    Log("  No DllGetActivationFactory in Microsoft.ui.xaml.dll");
                }
            }
            else
            {
                Log("  Could not load Microsoft.ui.xaml.dll for factory test");
            }
        }
        catch (Exception ex)
        {
            Log("  Factory test exception: " + ex.Message);
        }
        FlushLog();

        // Initialize CsWinRT COM wrappers
        Log("");
        Log("=== Initializing COM Wrappers ===");
        try
        {
            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            Log("  InitializeComWrappers: OK");
        }
        catch (Exception ex)
        {
            Log("  InitializeComWrappers FAILED: " + ex.GetType().Name +
                " (0x" + ex.HResult.ToString("X8") + "): " + ex.Message);
        }
        FlushLog();

        // Test CsWinRT activation path (RoGetActivationFactory)
        Log("");
        Log("=== RoGetActivationFactory Test ===");
        try
        {
            TestRoGetActivationFactory("Microsoft.UI.Xaml.Application");
            TestRoGetActivationFactory("Microsoft.UI.Dispatching.DispatcherQueueController");
        }
        catch (Exception ex)
        {
            Log("  Exception: " + ex.Message);
        }
        FlushLog();

        // Try getting the Application statics interface directly
        Log("");
        Log("=== Application Statics Test ===");
        try
        {
            // This is the exact call that crashed before
            var appType = typeof(global::Microsoft.UI.Xaml.Application);
            Log("  Application type loaded: " + appType.FullName);
            FlushLog();
        }
        catch (Exception ex)
        {
            Log("  Statics test failed: " + ex.GetType().Name +
                " (0x" + ex.HResult.ToString("X8") + "): " + ex.Message);
        }
        FlushLog();

        // Pre-tests removed: DispatcherQueue and Compositor both create OK
        // in WinPE but their teardown crashes CoreMessagingXP.dll,
        // preventing Application.Start from being reached.
        // Known: dxgi.dll throws 0x800706D9 (RPC_S_NO_ENDPOINTS_AVAILABLE)
        // during Compositor init but it's caught internally.

        // Start WinUI 3
        _vehPhase = "Application.Start";
        _vehCount = 0; // reset so we capture all exceptions in this phase

        // Detect silent process termination
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            Log("");
            Log("=== ProcessExit event fired ===");
            Log("  Environment.ExitCode: " + Environment.ExitCode);
            FlushLog();
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log("");
            Log("=== AppDomain.UnhandledException ===");
            Log("  IsTerminating: " + e.IsTerminating);
            if (e.ExceptionObject is Exception uex)
            {
                Log("  Type: " + uex.GetType().FullName);
                Log("  HResult: 0x" + uex.HResult.ToString("X8"));
                Log("  Message: " + uex.Message);
                Log("  Stack: " + uex.StackTrace);
            }
            else
            {
                Log("  ExceptionObject: " + e.ExceptionObject);
            }
            FlushLog();
        };

        // Catch unobserved Task exceptions (from fire-and-forget or
        // async void handlers that use Task internally)
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log("");
            Log("=== UnobservedTaskException ===");
            Log("  Type: " + e.Exception?.GetType().FullName);
            Log("  Message: " + e.Exception?.Message);
            if (e.Exception?.InnerExceptions != null)
            {
                foreach (var inner in e.Exception.InnerExceptions)
                {
                    Log("  Inner: " + inner.GetType().FullName + ": " + inner.Message);
                    Log("  InnerHR: 0x" + inner.HResult.ToString("X8"));
                }
            }
            e.SetObserved(); // Don't let it crash the process
            FlushLog();
        };

        Log("");
        if (Chronos.Common.Helpers.PeEnvironment.IsWinPE)
        {
            Log("=== WinPE Capability Probes ===");
            // VSS probe
            try
            {
                var vssService = new Chronos.Core.VSS.VssService();
                if (vssService.IsVssAvailable())
                {
                    Log("  VSS: Available (CreateVssBackupComponents succeeded)");
                }
                else
                {
                    Log("  VSS: Not available — " + (vssService.LastAvailabilityError ?? "unknown reason"));
                }
            }
            catch (Exception ex)
            {
                Log("  VSS: Not available (" + ex.GetType().Name + ": " + ex.Message + ")");
            }

            // WMI probe (in NoInlining method to avoid JIT crash)
            ProbeWmiCapabilities();

            // Volume API probe - test GetVolumeInformation
            try
            {
                var drives = System.IO.DriveInfo.GetDrives();
                Log("  DriveInfo: " + drives.Length + " drives found");
                foreach (var d in drives)
                {
                    try
                    {
                        if (d.IsReady)
                            Log("    " + d.Name + " Type=" + d.DriveType + " FS=" + d.DriveFormat + " Label=" + d.VolumeLabel);
                        else
                            Log("    " + d.Name + " Type=" + d.DriveType + " (not ready)");
                    }
                    catch (Exception ex)
                    {
                        Log("    " + d.Name + " ERROR: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("  DriveInfo: FAILED (" + ex.Message + ")");
            }

            // GetVolumeInformation probe on volume GUIDs
            try
            {
                var guids = Chronos.Native.Win32.VolumeApi.EnumerateVolumeGuids();
                Log("  VolumeGUIDs: " + guids.Count + " volumes found");
                foreach (var guid in guids)
                {
                    var devPath = Chronos.Native.Win32.VolumeApi.VolumeGuidToDevicePath(guid);
                    var extent = Chronos.Native.Win32.VolumeApi.GetVolumeDiskExtent(devPath);
                    var (fs, label) = Chronos.Native.Win32.VolumeApi.GetVolumeInformation(guid);
                    var diskNum = extent.HasValue ? extent.Value.DiskNumber.ToString() : "?";
                    Log("    " + guid + " -> Disk" + diskNum +
                        " FS=" + (fs ?? "(null)") + " Label=" + (label ?? "(null)"));
                }
            }
            catch (Exception ex)
            {
                Log("  VolumeGUIDs: FAILED (" + ex.Message + ")");
            }

            FlushLog();
        }

        Log("");
        Log("=== Calling Application.Start ===");
        FlushLog();

        try
        {
            global::Microsoft.UI.Xaml.Application.Start((p) =>
            {
                Log("  Inside Application.Start callback");
                FlushLog();

                var context = new global::Microsoft.UI.Dispatching
                    .DispatcherQueueSynchronizationContext(
                    global::Microsoft.UI.Dispatching.DispatcherQueue
                        .GetForCurrentThread());
                global::System.Threading.SynchronizationContext
                    .SetSynchronizationContext(context);

                Log("  Creating App instance...");
                FlushLog();

                var app = new App();

                Log("  App created successfully");

                // Hook XAML unhandled exception handler
                app.UnhandledException += (sender, args) =>
                {
                    Log("");
                    Log("=== XAML UnhandledException ===");
                    Log("Message: " + args.Message);
                    Log("Type: " + args.Exception?.GetType().FullName);
                    Log("HResult: 0x" + (args.Exception?.HResult ?? 0).ToString("X8"));
                    Log("Stack: " + args.Exception?.StackTrace);
                    if (args.Exception?.InnerException != null)
                    {
                        Log("Inner: " + args.Exception.InnerException.GetType().FullName
                            + ": " + args.Exception.InnerException.Message
                            + " (0x" + args.Exception.InnerException.HResult.ToString("X8") + ")");
                    }
                    FlushLog();
                    // Don't set Handled=true - let it crash so we can see it
                };
                Log("  UnhandledException handler attached");
                FlushLog();

                // Start a heartbeat timer to track how long the process lives
                // and periodically flush logs
                var heartbeatCount = 0;
                var heartbeatTimer = global::Microsoft.UI.Dispatching
                    .DispatcherQueue.GetForCurrentThread()
                    .CreateTimer();
                heartbeatTimer.Interval = TimeSpan.FromSeconds(2);
                heartbeatTimer.Tick += (t, o) =>
                {
                    heartbeatCount++;
                    Log("  [heartbeat #" + heartbeatCount + " at " +
                        DateTime.Now.ToString("HH:mm:ss") + "]");
                    FlushLog();
                    if (heartbeatCount >= 10)
                        heartbeatTimer.Stop(); // Stop after 20 seconds
                };
                heartbeatTimer.Start();
            });

            Log("");
            Log("=== Application.Start returned ===");
            Log("  (This means the message loop exited)");
            FlushLog();
        }
        catch (Exception ex)
        {
            Log("");
            Log("=== Application.Start FAILED ===");
            Log("Exception: " + ex.GetType().FullName);
            Log("HResult: 0x" + ex.HResult.ToString("X8"));
            Log("Message: " + ex.Message);
            Log("Stack: " + ex.StackTrace);
            if (ex.InnerException != null)
            {
                Log("Inner: " + ex.InnerException.GetType().FullName +
                    ": " + ex.InnerException.Message +
                    " (0x" + ex.InnerException.HResult.ToString("X8") + ")");
            }
            FlushLog();
            Console.Error.WriteLine("FATAL: " + ex.GetType().Name +
                " (0x" + ex.HResult.ToString("X8") + "): " + ex.Message);
            Console.Error.WriteLine("See chronos-startup.log for details");
        }
    }

    /// <summary>
    /// Parse PE import table and try to load each imported DLL.
    /// Reports which specific imports cannot be resolved.
    /// Recurses up to maxDepth levels to trace root-cause failures.
    /// </summary>
    private static readonly HashSet<string> _walked = new(
        StringComparer.OrdinalIgnoreCase);

    private static void WalkImports(string dllPath, string baseDir,
        string indent, int depth = 0)
    {
        if (depth > 3) return; // prevent infinite recursion
        var dllName = Path.GetFileName(dllPath);
        if (!_walked.Add(dllName)) return; // already walked

        try
        {
            var imports = ParsePeImports(dllPath);
            if (imports == null || imports.Length == 0)
            {
                Log(indent + "(no imports found)");
                return;
            }

            foreach (var imp in imports)
            {
                // Check if already loaded in process
                var existing = GetModuleHandleW(imp);
                if (existing != IntPtr.Zero)
                    continue;

                IntPtr h;
                var localPath = Path.Combine(baseDir, imp);
                string? resolvedPath = null;
                if (File.Exists(localPath))
                {
                    resolvedPath = localPath;
                    h = LoadLibraryExW(localPath, IntPtr.Zero,
                        LOAD_WITH_ALTERED_SEARCH_PATH);
                }
                else
                {
                    // API set or system DLL
                    h = LoadLibraryW(imp);
                }

                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log(indent + "MISSING: " + imp + " (error " + err + ")");

                    // If the file exists but fails to load (126),
                    // recurse to find ITS missing dependency
                    if (err == 126 && resolvedPath != null)
                    {
                        WalkImports(resolvedPath, baseDir,
                            indent + "  ", depth + 1);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log(indent + "Import parse error: " + ex.Message);
        }
    }

    /// <summary>
    /// Parse PE import directory entries from a DLL on disk.
    /// Returns array of imported DLL names.
    /// </summary>
    private static string[]? ParsePeImports(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 0x40) return null;

        int peOff = BitConverter.ToInt32(data, 0x3C);
        if (peOff <= 0 || peOff + 24 >= data.Length) return null;

        ushort magic = BitConverter.ToUInt16(data, peOff + 24);
        // PE32+ (64-bit): import dir at offset 120,  PE32: at offset 104
        int importDirOffset = magic == 0x20b ? 120 : 104;
        uint importRva = BitConverter.ToUInt32(data, peOff + 24 + importDirOffset);
        if (importRva == 0) return null;

        int numSections = BitConverter.ToUInt16(data, peOff + 6);
        int optHdrSize = BitConverter.ToUInt16(data, peOff + 20);
        int sectStart = peOff + 24 + optHdrSize;

        var result = new System.Collections.Generic.List<string>();

        for (int i = 0; i < numSections; i++)
        {
            int so = sectStart + i * 40;
            uint sVa = BitConverter.ToUInt32(data, so + 12);
            uint sVs = BitConverter.ToUInt32(data, so + 8);
            uint sRp = BitConverter.ToUInt32(data, so + 20);

            if (importRva >= sVa && importRva < sVa + sVs)
            {
                int fileOff = (int)(sRp + importRva - sVa);
                while (fileOff + 20 <= data.Length)
                {
                    uint nameRva = BitConverter.ToUInt32(data, fileOff + 12);
                    if (nameRva == 0) break;
                    int nameOff = (int)(sRp + nameRva - sVa);
                    if (nameOff >= data.Length) break;
                    int end = nameOff;
                    while (end < data.Length && data[end] != 0) end++;
                    result.Add(Encoding.ASCII.GetString(data, nameOff, end - nameOff));
                    fileOff += 20;
                }
                break;
            }
        }

        return result.ToArray();
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetActivationFactoryDelegate(
        IntPtr activatableClassId, out IntPtr factory);

    private static void TestActivationFactory(IntPtr procAddr, string className)
    {
        try
        {
            var dgaf = Marshal.GetDelegateForFunctionPointer
                <DllGetActivationFactoryDelegate>(procAddr);
            int hr = WindowsCreateString(className, className.Length,
                out IntPtr hstring);
            if (hr != 0)
            {
                Log("  " + className + ": WindowsCreateString failed 0x" +
                    hr.ToString("X8"));
                return;
            }

            hr = dgaf(hstring, out IntPtr factory);
            WindowsDeleteString(hstring);

            if (hr == 0 && factory != IntPtr.Zero)
            {
                Log("  " + className + ": OK (factory=0x" +
                    factory.ToString("X") + ")");
                Marshal.Release(factory);
            }
            else
            {
                Log("  " + className + ": FAILED hr=0x" +
                    hr.ToString("X8"));
            }
        }
        catch (Exception ex)
        {
            Log("  " + className + ": EXCEPTION " + ex.Message);
        }
    }

    // Native vectored exception handler to log crash details
    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecordPtr;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        // ExceptionInformation follows but we don't need it
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_POINTERS
    {
        public IntPtr ExceptionRecord; // EXCEPTION_RECORD*
        public IntPtr ContextRecord;   // CONTEXT*
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VectoredExceptionDelegate(IntPtr exceptionPointers);

    // Must be kept alive to prevent GC of the delegate
    private static VectoredExceptionDelegate? _vehDelegate;
    private static IntPtr _vehHandle;
    private static int _vehCount;
    private static string _vehPhase = "init";

    private const int EXCEPTION_EXECUTE_HANDLER = 1;

    private static int TopLevelExceptionFilter(IntPtr pExceptionInfo)
    {
        try
        {
            var ptrs = Marshal.PtrToStructure<EXCEPTION_POINTERS>(pExceptionInfo);
            var record = Marshal.PtrToStructure<EXCEPTION_RECORD>(ptrs.ExceptionRecord);

            string codeName = record.ExceptionCode switch
            {
                0xC0000005 => "ACCESS_VIOLATION",
                0xC000001D => "ILLEGAL_INSTRUCTION",
                0xC0000374 => "HEAP_CORRUPTION",
                0x80000003 => "BREAKPOINT",
                0xC00000FD => "STACK_OVERFLOW",
                _ => $"0x{record.ExceptionCode:X8}"
            };

            string moduleInfo = FindModuleForAddress(record.ExceptionAddress);

            Log("");
            Log("=== FATAL NATIVE CRASH (TopLevelExceptionFilter) ===");
            Log($"  Code: {codeName} (0x{record.ExceptionCode:X8})");
            Log($"  Address: 0x{record.ExceptionAddress:X}");
            Log($"  Module: {moduleInfo}");
            Log($"  Flags: {record.ExceptionFlags}");
            Log($"  Phase: {_vehPhase}");
            FlushLog();
        }
        catch
        {
            // Last resort: write directly
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "chronos-startup.log");
                File.AppendAllText(path, "\n=== FATAL NATIVE CRASH (TopLevelExceptionFilter, inner catch) ===\n");
            }
            catch { }
        }
        return EXCEPTION_EXECUTE_HANDLER;
    }

    private static int VectoredExceptionHandler(IntPtr pExceptionPointers)
    {
        try
        {
            var ptrs = Marshal.PtrToStructure<EXCEPTION_POINTERS>(
                pExceptionPointers);
            var record = Marshal.PtrToStructure<EXCEPTION_RECORD>(
                ptrs.ExceptionRecord);

            // Only log "real" exceptions, not first-chance CLR exceptions
            // 0xE0434352 = managed exception, 0x406D1388 = thread name
            if (record.ExceptionCode == 0xE0434352 ||
                record.ExceptionCode == 0x406D1388 ||
                record.ExceptionCode == 0x40010006) // DBG_PRINTEXCEPTION
            {
                return EXCEPTION_CONTINUE_SEARCH;
            }

            _vehCount++;
            // Limit to 20 entries to avoid filling the log
            if (_vehCount > 20) return EXCEPTION_CONTINUE_SEARCH;

            bool noncontinuable =
                (record.ExceptionFlags & 0x01) != 0;
            bool unwinding =
                (record.ExceptionFlags & 0x02) != 0;

            string codeName = record.ExceptionCode switch
            {
                0xC0000005 => "ACCESS_VIOLATION",
                0xC0000374 => "HEAP_CORRUPTION",
                0xC00000FD => "STACK_OVERFLOW",
                0xC0000409 => "STACK_BUFFER_OVERRUN (__fastfail)",
                0x80000003 => "BREAKPOINT",
                0xC0000096 => "PRIVILEGED_INSTRUCTION",
                0xE06D7363 => "CXX_EXCEPTION (C++ throw)",
                0xC06D007E => "DELAY_LOAD_FAILED (ERROR_MOD_NOT_FOUND)",
                0xC06D007F => "DELAY_LOAD_FAILED (ERROR_PROC_NOT_FOUND)",
                _ => "0x" + record.ExceptionCode.ToString("X8")
            };

            Log("");
            Log("=== NATIVE EXCEPTION (VEH #" + _vehCount +
                ", phase=" + _vehPhase + ") ===");
            Log("ExceptionCode: " + codeName);
            Log("ExceptionAddress: 0x" +
                record.ExceptionAddress.ToString("X"));
            Log("ExceptionFlags: " + record.ExceptionFlags +
                (noncontinuable ? " [NONCONTINUABLE]" : "") +
                (unwinding ? " [UNWINDING]" : ""));
            Log("NumberParameters: " + record.NumberParameters);

            // For C++ exceptions (0xE06D7363), read the parameters:
            // params[0] = magic (0x19930520)
            // params[1] = pointer to thrown object
            // params[2] = pointer to _ThrowInfo
            // params[3] = HMODULE of throwing module (x64 only)
            if (record.ExceptionCode == 0xE06D7363 &&
                record.NumberParameters >= 3)
            {
                // Read ExceptionInformation array from EXCEPTION_RECORD
                // Offset: Code(4) + Flags(4) + RecordPtr(8) +
                //         Address(8) + NumParams(4) + pad(4) = 32
                IntPtr paramsBase = ptrs.ExceptionRecord + 32;
                long magic = Marshal.ReadInt64(paramsBase, 0);
                long thrownObjPtr = Marshal.ReadInt64(paramsBase, 8);
                long throwInfoPtr = Marshal.ReadInt64(paramsBase, 16);

                Log("CXX param[0] (magic): 0x" + magic.ToString("X"));
                Log("CXX param[1] (thrown obj): 0x" +
                    thrownObjPtr.ToString("X"));
                Log("CXX param[2] (throwInfo): 0x" +
                    throwInfoPtr.ToString("X"));

                if (record.NumberParameters >= 4)
                {
                    long moduleBase = Marshal.ReadInt64(paramsBase, 24);
                    Log("CXX param[3] (module base): 0x" +
                        moduleBase.ToString("X"));

                    // Get module name from HMODULE
                    if (moduleBase != 0)
                    {
                        var name = GetModuleName((IntPtr)moduleBase);
                        Log("Throwing module: " + name);
                    }
                }

                // Try to read HRESULT from thrown object
                // winrt::hresult_error stores HRESULT at offset 8
                // std::exception_ptr varies, try common offsets
                if (thrownObjPtr != 0)
                {
                    try
                    {
                        int hr0 = Marshal.ReadInt32((IntPtr)thrownObjPtr, 0);
                        int hr8 = Marshal.ReadInt32((IntPtr)thrownObjPtr, 8);
                        int hr16 = Marshal.ReadInt32((IntPtr)thrownObjPtr, 16);
                        Log("Thrown obj [0]: 0x" + hr0.ToString("X8"));
                        Log("Thrown obj [8]: 0x" + hr8.ToString("X8"));
                        Log("Thrown obj [16]: 0x" + hr16.ToString("X8"));
                    }
                    catch { /* can't read thrown object */ }
                }
            }

            // For delay-load failures (0xC06D007E / 0xC06D007F),
            // param[0] = pointer to DelayLoadInfo struct
            // DelayLoadInfo x64 layout:
            //   offset  0: DWORD cb (4) + pad(4)
            //   offset  8: PCImgDelayDescr pidd (8)
            //   offset 16: FARPROC* ppfn (8)
            //   offset 24: LPCSTR szDll (8) - ANSI DLL name
            //   offset 32: BOOL fImportByName (4) + pad(4)
            //   offset 40: LPCSTR szProcName / DWORD dwOrdinal (8)
            //   offset 48: HMODULE hmodCur (8)
            //   offset 56: FARPROC pfnCur (8)
            //   offset 64: DWORD dwLastError (4)
            if ((record.ExceptionCode == 0xC06D007E ||
                 record.ExceptionCode == 0xC06D007F) &&
                record.NumberParameters >= 1)
            {
                IntPtr paramsBase = ptrs.ExceptionRecord + 32;
                long delayLoadInfoPtr = Marshal.ReadInt64(paramsBase, 0);
                if (delayLoadInfoPtr != 0)
                {
                    try
                    {
                        IntPtr dli = (IntPtr)delayLoadInfoPtr;
                        // Read szDll (ANSI string pointer at offset 24)
                        IntPtr szDllPtr = Marshal.ReadIntPtr(dli, 24);
                        string dllName = szDllPtr != IntPtr.Zero
                            ? Marshal.PtrToStringAnsi(szDllPtr) ?? "(null)"
                            : "(null)";
                        Log("Delay-load DLL: " + dllName);

                        // Read fImportByName at offset 32
                        int fImportByName = Marshal.ReadInt32(dli, 32);
                        if (fImportByName != 0)
                        {
                            // szProcName at offset 40
                            IntPtr szProcPtr = Marshal.ReadIntPtr(dli, 40);
                            string procName = szProcPtr != IntPtr.Zero
                                ? Marshal.PtrToStringAnsi(szProcPtr)
                                    ?? "(null)"
                                : "(null)";
                            Log("Delay-load proc: " + procName);
                        }
                        else
                        {
                            int ordinal = Marshal.ReadInt32(dli, 40);
                            Log("Delay-load ordinal: " + ordinal);
                        }

                        // dwLastError at offset 64
                        int lastErr = Marshal.ReadInt32(dli, 64);
                        Log("Delay-load lastError: " + lastErr +
                            " (0x" + lastErr.ToString("X") + ")");

                        // hmodCur at offset 48
                        IntPtr hmod = Marshal.ReadIntPtr(dli, 48);
                        if (hmod != IntPtr.Zero)
                        {
                            Log("Delay-load module handle: " +
                                GetModuleName(hmod));
                        }
                        else
                        {
                            Log("Delay-load module handle: NULL" +
                                " (DLL not loaded)");
                        }
                    }
                    catch (Exception dlEx)
                    {
                        Log("Delay-load info read failed: " +
                            dlEx.Message);
                    }
                }
            }

            // Resolve which module the exception address is in
            string addrModule = FindModuleForAddress(
                record.ExceptionAddress);
            Log("Exception in module: " + addrModule);

            FlushLog();
        }
        catch
        {
            // Best effort - we're in a crash handler
        }

        return EXCEPTION_CONTINUE_SEARCH;
    }

    /// <summary>
    /// Get module filename from an HMODULE.
    /// </summary>
    private static string GetModuleName(IntPtr hModule)
    {
        try
        {
            var sb = new StringBuilder(512);
            uint len = GetModuleFileNameExW(
                GetCurrentProcess(), hModule, sb, 512);
            return len > 0 ? Path.GetFileName(sb.ToString()) : "(unknown)";
        }
        catch { return "(error)"; }
    }

    /// <summary>
    /// Find which loaded module contains a given address.
    /// </summary>
    private static string FindModuleForAddress(IntPtr address)
    {
        try
        {
            IntPtr proc = GetCurrentProcess();
            var modules = new IntPtr[1024];
            if (!EnumProcessModules(proc, modules,
                (uint)(modules.Length * IntPtr.Size), out uint needed))
                return "(EnumProcessModules failed)";

            int count = (int)(needed / IntPtr.Size);
            for (int i = 0; i < count; i++)
            {
                if (modules[i] == IntPtr.Zero) continue;
                if (GetModuleInformation(proc, modules[i],
                    out MODULEINFO info, (uint)Marshal.SizeOf<MODULEINFO>()))
                {
                    long baseAddr = info.lpBaseOfDll.ToInt64();
                    long endAddr = baseAddr + info.SizeOfImage;
                    long target = address.ToInt64();
                    if (target >= baseAddr && target < endAddr)
                    {
                        return GetModuleName(modules[i]) +
                            " (base=0x" + baseAddr.ToString("X") +
                            ", offset=0x" +
                            (target - baseAddr).ToString("X") + ")";
                    }
                }
            }
            return "(not found in any module)";
        }
        catch { return "(error during lookup)"; }
    }

    private static unsafe void TestRoGetActivationFactory(string className)
    {
        int hr = WindowsCreateString(className, className.Length,
            out IntPtr hstring);
        if (hr != 0)
        {
            Log("  " + className + ": WindowsCreateString failed 0x" +
                hr.ToString("X8"));
            return;
        }

        Guid iid = new Guid("00000035-0000-0000-C000-000000000046"); // IActivationFactory
        IntPtr factory = IntPtr.Zero;
        hr = RoGetActivationFactory(hstring, &iid, &factory);
        WindowsDeleteString(hstring);

        if (hr == 0 && factory != IntPtr.Zero)
        {
            Log("  RoGetActivationFactory('" + className +
                "'): OK (0x" + factory.ToString("X") + ")");
            Marshal.Release(factory);
        }
        else
        {
            Log("  RoGetActivationFactory('" + className +
                "'): FAILED hr=0x" + hr.ToString("X8"));
        }
    }
}
