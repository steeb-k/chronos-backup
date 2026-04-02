using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Chronos.Core.VSS;

/// <summary>
/// VSS implementation using native Windows VSS API (VssApi.dll) via pure P/Invoke.
/// Works on x86, x64, and ARM64 without third-party native binaries.
/// </summary>
public sealed class VssService : IVssService
{
    private const uint WaitObject0 = 0;
    private const uint QsAllinput = 0x04FF;
    private const uint PmRemove = 0x0001;

    /// <summary>
    /// Last error details from IsVssAvailable() — useful for diagnostics.
    /// </summary>
    public string? LastAvailabilityError { get; private set; }

    public bool IsVssAvailable()
    {
        LastAvailabilityError = null;

        // 1. Check that vssapi.dll exists at all
        try
        {
            var sysRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var vssApiPath = Path.Combine(sysRoot, "System32", "vssapi.dll");
            if (!File.Exists(vssApiPath))
            {
                LastAvailabilityError = $"vssapi.dll not found at {vssApiPath}";
                Log.Debug("VSS unavailable: {Error}", LastAvailabilityError);
                return false;
            }
        }
        catch (Exception ex)
        {
            LastAvailabilityError = $"Failed to check vssapi.dll: {ex.Message}";
            return false;
        }

        // 2. Try to create VSS backup components (the real test)
        try
        {
            VssNative.CreateVssBackupComponents(out IVssBackupComponents vss);
            if (vss != null)
            {
                Marshal.ReleaseComObject(vss);
                return true;
            }
            else
            {
                LastAvailabilityError = "CreateVssBackupComponents returned null";
                Log.Debug("VSS unavailable: {Error}", LastAvailabilityError);
                return false;
            }
        }
        catch (DllNotFoundException ex)
        {
            LastAvailabilityError = $"VssApi.dll could not be loaded: {ex.Message}";
        }
        catch (EntryPointNotFoundException ex)
        {
            LastAvailabilityError = $"CreateVssBackupComponents not found in VssApi.dll: {ex.Message}";
        }
        catch (COMException ex)
        {
            LastAvailabilityError = $"COM error 0x{ex.HResult:X8}: {ex.Message}";
        }
        catch (Exception ex)
        {
            LastAvailabilityError = $"{ex.GetType().Name}: {ex.Message} (HR=0x{ex.HResult:X8})";
        }

        Log.Debug("VSS unavailable: {Error}", LastAvailabilityError);
        return false;
    }

    public async Task<IVssSnapshotSet> CreateSnapshotSetAsync(IReadOnlyList<string> volumePaths, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        if (volumePaths == null || volumePaths.Count == 0)
            throw new ArgumentException("At least one volume path required.", nameof(volumePaths));

        var normalized = volumePaths.Select(NormalizeVolumeForVss).Distinct().ToList();
        if (normalized.Count == 0)
            throw new ArgumentException("No valid volume paths.", nameof(volumePaths));

        // VSS COM interfaces require an STA thread. Thread pool threads (used by Task.Run)
        // are MTA, which causes InitializeForBackup to fail with E_INVALIDARG (0x80070057)
        // — especially in WinPE where COM apartment enforcement is strict. Spin up a
        // dedicated STA thread for the entire VSS operation. The thread stays alive with a
        // message pump until Dispose deletes the snapshot set on the same apartment.
        var tcs = new TaskCompletionSource<IVssSnapshotSet>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.SetCanceled(cancellationToken);
                return;
            }

            IVssBackupComponents? vss = null;
            ManualResetEvent? disposeSignal = null;
            ManualResetEvent? staCleanupDone = null;
            try
            {
                VssNative.CreateVssBackupComponents(out vss);
                if (vss == null)
                {
                    tcs.SetException(new InvalidOperationException("Failed to create VSS backup components."));
                    return;
                }

                int hr = vss.InitializeForBackup(null);
                if (hr < 0)
                    throw new InvalidOperationException($"VSS InitializeForBackup failed: 0x{hr:X8}");

                hr = vss.SetContext(VssNative.VSS_CTX_BACKUP);
                if (hr < 0)
                    throw new InvalidOperationException($"VSS SetContext failed: 0x{hr:X8}");

                progress?.Report("Gathering writer metadata...");
                hr = vss.GatherWriterMetadata(out IVssAsync gatherAsync);
                if (hr < 0)
                    throw new InvalidOperationException($"VSS GatherWriterMetadata failed: 0x{hr:X8}");
                CompleteVssAsync(gatherAsync, "GatherWriterMetadata");

                hr = vss.FreeWriterMetadata();
                if (hr < 0)
                    Log.Warning("VSS FreeWriterMetadata returned 0x{Hr:X8}", (uint)hr);

                progress?.Report("Adding volumes to snapshot set...");
                hr = vss.StartSnapshotSet(out Guid snapshotSetId);
                if (hr < 0)
                    throw new InvalidOperationException($"VSS StartSnapshotSet failed: 0x{hr:X8}");

                var snapshotIds = new List<Guid>();

                foreach (var vol in normalized)
                {
                    try
                    {
                        hr = vss.AddToSnapshotSet(vol, Guid.Empty, out Guid id);
                        if (hr >= 0)
                            snapshotIds.Add(id);
                        else
                            Log.Warning("Could not add volume {Volume} to snapshot set: 0x{Hr:X8}", vol, (uint)hr);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not add volume {Volume} to snapshot set", vol);
                    }
                }

                if (snapshotIds.Count == 0)
                    throw new InvalidOperationException("No volumes could be added to the snapshot set.");

                progress?.Report("Preparing for backup...");
                vss.SetBackupState(false, false, VssNative.VSS_BT_FULL, false);
                hr = vss.PrepareForBackup(out IVssAsync prepareAsync);
                if (hr < 0)
                    throw new InvalidOperationException($"VSS PrepareForBackup failed: 0x{hr:X8}");
                try
                {
                    CompleteVssAsync(prepareAsync, "PrepareForBackup");
                }
                catch (COMException ex) when (ex.HResult == VssNative.VSS_E_WRITER_INFRASTRUCTURE)
                {
                    // VSS_E_WRITER_INFRASTRUCTURE: writer services are unavailable (common on
                    // ARM64). The snapshot can still proceed — it will be crash-consistent
                    // rather than application-consistent, which is acceptable for disk imaging.
                    Log.Warning("VSS PrepareForBackup: writer infrastructure error (0x80042316). " +
                                "Proceeding without writer quiescence — snapshot will be crash-consistent.");
                }

                progress?.Report("Creating snapshot...");
                hr = vss.DoSnapshotSet(out IVssAsync snapshotAsync);
                if (hr < 0)
                    throw new InvalidOperationException($"VSS DoSnapshotSet failed: 0x{hr:X8}");
                try
                {
                    CompleteVssAsync(snapshotAsync, "DoSnapshotSet");
                }
                catch (COMException ex) when (ex.HResult == VssNative.VSS_E_WRITER_INFRASTRUCTURE)
                {
                    // Writers failed during freeze/thaw but the VSS provider snapshot is
                    // typically still valid (crash-consistent). Continue to retrieve the
                    // snapshot device path.
                    Log.Warning("VSS DoSnapshotSet: writer infrastructure error (0x80042316). " +
                                "Snapshot created as crash-consistent (writer quiescence not achieved).");
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    Marshal.ReleaseComObject(vss);
                    vss = null;
                    tcs.SetCanceled(cancellationToken);
                    return;
                }

                var originalToSnapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < snapshotIds.Count; i++)
                {
                    hr = vss.GetSnapshotProperties(snapshotIds[i], out VSS_SNAPSHOT_PROP props);
                    if (hr >= 0)
                    {
                        try
                        {
                            string? devicePath = VssNative.GetSnapshotDeviceObject(ref props);
                            if (!string.IsNullOrEmpty(devicePath))
                            {
                                string snapshotPath = ToBackslashPath(devicePath);
                                string addedVolume = normalized[i];
                                var drive = addedVolume.TrimEnd('\\');
                                // Add multiple key formats so GetSnapshotPath can resolve any volume path form
                                originalToSnapshot[drive] = snapshotPath;
                                originalToSnapshot[addedVolume] = snapshotPath;
                                originalToSnapshot[@"\\.\" + drive] = snapshotPath;
                                originalToSnapshot[@"\\.\" + drive + @"\"] = snapshotPath;
                                if (drive.Length >= 2 && drive[1] == ':')
                                    originalToSnapshot[drive + @"\"] = snapshotPath;
                            }
                        }
                        finally
                        {
                            VssNative.VssFreeSnapshotProperties(ref props);
                        }
                    }
                }

                Log.Information("VSS snapshot set created: {Count} volume(s)", originalToSnapshot.Count);

                disposeSignal = new ManualResetEvent(false);
                staCleanupDone = new ManualResetEvent(false);
                Guid setId = snapshotSetId;
                tcs.SetResult(new VssSnapshotSetImpl(originalToSnapshot, disposeSignal, staCleanupDone));

                PumpStaUntilSignaled(disposeSignal);

                hr = vss.DeleteSnapshots(setId, VssNative.VSS_OBJECT_SNAPSHOT_SET, false, out _, out _);
                if (hr < 0)
                    Log.Warning("VSS DeleteSnapshots returned 0x{Hr:X8}", (uint)hr);
                else
                    Log.Debug("VSS snapshot set deleted");
            }
            catch (Exception ex)
            {
                if (vss != null)
                {
                    try
                    {
                        Marshal.ReleaseComObject(vss);
                    }
                    catch (Exception releaseEx)
                    {
                        Log.Debug(releaseEx, "VSS: Release backup components after error");
                    }

                    vss = null;
                }

                if (!tcs.Task.IsCompleted)
                    tcs.SetException(ex);
                else
                    Log.Error(ex, "VSS STA thread failed after snapshot set was created");
            }
            finally
            {
                if (vss != null)
                {
                    try
                    {
                        Marshal.ReleaseComObject(vss);
                    }
                    catch (Exception releaseEx)
                    {
                        Log.Debug(releaseEx, "VSS: Release backup components in finally");
                    }
                }

                staCleanupDone?.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "VssSnapshotSTA";
        thread.Start();

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes an IVssAsync operation: Wait, QueryStatus (required by VSS), release.
    /// </summary>
    private static void CompleteVssAsync(IVssAsync async, string operationName)
    {
        ArgumentNullException.ThrowIfNull(async);
        try
        {
            int hrWait = async.Wait(VssNative.INFINITE);
            if (hrWait < 0)
                throw new COMException($"VSS {operationName}: IVssAsync.Wait failed: 0x{hrWait:X8}", hrWait);

            int hrOp = 0;
            int reserved = 0;
            int hrQs = async.QueryStatus(out hrOp, out reserved);
            if (hrQs < 0)
                throw new COMException($"VSS {operationName}: IVssAsync.QueryStatus failed: 0x{hrQs:X8}", hrQs);
            if (hrOp < 0)
                throw new COMException(FormatVssOperationError(operationName, hrOp), hrOp);
        }
        finally
        {
            Marshal.ReleaseComObject(async);
        }
    }

    private static string FormatVssOperationError(string operationName, int hrOp)
    {
        string hint = hrOp switch
        {
            unchecked((int)0x80070005) => " (E_ACCESSDENIED — check concurrent VSS backups, writer/DCOM permissions.)",
            unchecked((int)0x8004230F) => " (VSS_E_UNEXPECTED_WRITER_ERROR)",
            VssNative.VSS_E_WRITER_INFRASTRUCTURE => " (VSS_E_WRITER_INFRASTRUCTURE — writer services not operating properly, common on ARM64.)",
            unchecked((int)0x80042318) => " (VSS_E_WRITER_STATUS_NOT_AVAILABLE)",
            _ => string.Empty
        };
        return $"VSS {operationName} async operation failed: 0x{hrOp:X8}{hint}";
    }

    /// <summary>
    /// Runs a minimal STA message pump until <paramref name="disposeSignal"/> is set, so COM callbacks can complete.
    /// </summary>
    private static void PumpStaUntilSignaled(ManualResetEvent disposeSignal)
    {
        IntPtr handle = disposeSignal.SafeWaitHandle.DangerousGetHandle();
        var handles = new[] { handle };

        while (!disposeSignal.WaitOne(0))
        {
            uint wake = MsgWaitForMultipleObjects(1, handles, false, unchecked((uint)-1), QsAllinput);
            if (wake == WaitObject0)
                break;

            // WAIT_OBJECT_0 + nCount: input (e.g. messages) is available
            if (wake == WaitObject0 + 1)
            {
                while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PmRemove))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            else
            {
                Log.Debug("MsgWaitForMultipleObjects returned unexpected value {Wake}", wake);
                Thread.Sleep(10);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[] pHandles, [MarshalAs(UnmanagedType.Bool)] bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    /// <summary>
    /// Normalize for VSS AddToSnapshotSet: needs "X:\" or "\\?\Volume{guid}\" format.
    /// </summary>
    private static string NormalizeVolumeForVss(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        path = path.Trim();
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            path = path.Substring(4);
        if (path.Length >= 2 && path[1] == ':' && path.Length == 2)
            path = path + "\\";
        if (path.Length >= 2 && path[1] == ':' && !path.EndsWith("\\"))
            path = path + "\\";
        return path;
    }

    /// <summary>
    /// Convert device path to CreateFile-compatible form. "\??\GLOBALROOT\..." -> "\\.\GLOBALROOT\..."
    /// </summary>
    private static string ToBackslashPath(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return string.Empty;
        if (devicePath.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            return @"\\.\" + devicePath.Substring(4);
        if (devicePath.StartsWith(@"\\?\"))
            return @"\\.\" + devicePath.Substring(4);
        if (!devicePath.StartsWith(@"\\.\"))
            return @"\\.\" + devicePath;
        return devicePath;
    }

    private sealed class VssSnapshotSetImpl : IVssSnapshotSet
    {
        private readonly IReadOnlyDictionary<string, string> _originalToSnapshot;
        private readonly ManualResetEvent _disposeSignal;
        private readonly ManualResetEvent _staCleanupDone;
        private bool _disposed;

        public VssSnapshotSetImpl(
            IReadOnlyDictionary<string, string> originalToSnapshot,
            ManualResetEvent disposeSignal,
            ManualResetEvent staCleanupDone)
        {
            _originalToSnapshot = originalToSnapshot;
            _disposeSignal = disposeSignal;
            _staCleanupDone = staCleanupDone;
        }

        public string? GetSnapshotPath(string originalVolumePath)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IVssSnapshotSet));
            if (string.IsNullOrEmpty(originalVolumePath)) return null;
            var trimmed = originalVolumePath.Trim().TrimEnd('\\');
            if (_originalToSnapshot.TryGetValue(originalVolumePath, out var p)) return p;
            if (_originalToSnapshot.TryGetValue(trimmed, out p)) return p;
            if (trimmed.Length >= 2 && trimmed[1] == ':' && _originalToSnapshot.TryGetValue(@"\\.\" + trimmed, out p)) return p;
            if (trimmed.Length >= 2 && trimmed[1] == ':' && _originalToSnapshot.TryGetValue(trimmed + @"\", out p)) return p;
            if (originalVolumePath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) && _originalToSnapshot.TryGetValue(originalVolumePath.TrimEnd('\\'), out p)) return p;
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _disposeSignal.Set();
            if (!_staCleanupDone.WaitOne(TimeSpan.FromSeconds(10)))
                Log.Warning("VSS STA cleanup did not complete within 10 seconds");
        }
    }
}
