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

        return await Task.Run(() =>
        {
            VssNative.CreateVssBackupComponents(out IVssBackupComponents vss);
            if (vss == null)
                throw new InvalidOperationException("Failed to create VSS backup components.");

            try
            {
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
                gatherAsync.Wait(VssNative.INFINITE);
                Marshal.ReleaseComObject(gatherAsync);

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
                prepareAsync.Wait(VssNative.INFINITE);
                Marshal.ReleaseComObject(prepareAsync);

                progress?.Report("Creating snapshot...");
                hr = vss.DoSnapshotSet(out IVssAsync snapshotAsync);
                if (hr < 0)
                    throw new InvalidOperationException($"VSS DoSnapshotSet failed: 0x{hr:X8}");
                snapshotAsync.Wait(VssNative.INFINITE);
                Marshal.ReleaseComObject(snapshotAsync);

                cancellationToken.ThrowIfCancellationRequested();

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
                return new VssSnapshotSetImpl(vss, snapshotSetId, originalToSnapshot);
            }
            catch
            {
                Marshal.ReleaseComObject(vss);
                throw;
            }
        }, cancellationToken);
    }

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
        private IVssBackupComponents? _vss;
        private readonly Guid _snapshotSetId;
        private readonly IReadOnlyDictionary<string, string> _originalToSnapshot;
        private bool _disposed;

        public VssSnapshotSetImpl(IVssBackupComponents vss, Guid snapshotSetId, IReadOnlyDictionary<string, string> originalToSnapshot)
        {
            _vss = vss;
            _snapshotSetId = snapshotSetId;
            _originalToSnapshot = originalToSnapshot;
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
            var vss = Interlocked.Exchange(ref _vss, null);
            if (vss != null)
            {
                try
                {
                    vss.DeleteSnapshots(_snapshotSetId, VssNative.VSS_OBJECT_SNAPSHOT_SET, false, out _, out _);
                    Log.Debug("VSS snapshot set deleted");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete VSS snapshot set");
                }
                finally
                {
                    Marshal.ReleaseComObject(vss);
                }
            }
            _disposed = true;
        }
    }
}
