using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Chronos.Native.VirtDisk;
using Serilog;

namespace Chronos.Core.VirtualDisk;

/// <summary>
/// Attached VHDX handle - dispose to detach and close.
/// </summary>
internal sealed class AttachedVhdx : IAttachedVhdx
{
    private SafeFileHandle _handle;
    private bool _disposed;

    public string PhysicalPath { get; }

    public AttachedVhdx(SafeFileHandle handle, string physicalPath)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        PhysicalPath = physicalPath ?? throw new ArgumentNullException(nameof(physicalPath));
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (_handle != null && !_handle.IsInvalid)
            {
                Log.Debug("AttachedVhdx: calling DetachVirtualDisk for {Path}", PhysicalPath);
                uint result = VirtualDiskInterop.DetachVirtualDisk(_handle, 0, 0);
                if (result != 0)
                {
                    Log.Warning("DetachVirtualDisk failed: result={Result} for {Path}", result, PhysicalPath);
                }
                else
                {
                    Log.Debug("AttachedVhdx: DetachVirtualDisk succeeded");
                }
            }
        }
        finally
        {
            _handle?.Dispose();
            _handle = null!;
            _disposed = true;
        }
    }
}

/// <summary>
/// Implementation of virtual disk operations using Windows Virtual Disk API
/// </summary>
public class VirtualDiskService : IVirtualDiskService
{
    private readonly Dictionary<string, SafeFileHandle> _mountedDisks = new();
    private readonly object _lock = new();

    public async Task CreateDynamicVhdxAsync(string path, long maxSizeBytes, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            Log.Debug("CreateVirtualDisk: path={Path}, maxSizeBytes={Size}", path, maxSizeBytes);
            var storageType = new VirtualDiskInterop.VirtualStorageTypeStruct
            {
                DeviceId = VirtualDiskInterop.VirtualStorageType.VHDX,
                VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B")
            };

            var parameters = new VirtualDiskInterop.CreateVirtualDiskParametersV2
            {
                Version = VirtualDiskInterop.CreateVirtualDiskVersion.Version2,
                UniqueId = Guid.NewGuid(),
                MaximumSize = (ulong)maxSizeBytes,
                BlockSizeInBytes = 32 * 1024 * 1024, // 32 MB block size
                SectorSizeInBytes = 512,
                PhysicalSectorSizeInBytes = 512,
                ParentPath = IntPtr.Zero,
                SourcePath = IntPtr.Zero
            };

            uint result = VirtualDiskInterop.CreateVirtualDisk(
                ref storageType,
                path,
                VirtualDiskInterop.VirtualDiskAccessMask.None,
                IntPtr.Zero,
                VirtualDiskInterop.CreateVirtualDiskFlags.None,
                0,
                ref parameters,
                IntPtr.Zero,
                out SafeFileHandle handle);

            int win32 = Marshal.GetLastWin32Error();
            if (result != 0)
            {
                Log.Error("CreateVirtualDisk FAILED: result={Result}, Win32={Win32} (0x{Win32X}), path={Path}", result, win32, win32.ToString("X"), path);
                throw new InvalidOperationException($"Failed to create virtual disk: Error {result}");
            }
            Log.Debug("CreateVirtualDisk succeeded");
            handle.Dispose();
        }, cancellationToken);
    }

    public async Task<IntPtr> OpenVhdxAsync(string path, bool readOnly = true)
    {
        return await Task.Run(() =>
        {
            var storageType = new VirtualDiskInterop.VirtualStorageTypeStruct
            {
                DeviceId = VirtualDiskInterop.VirtualStorageType.VHDX,
                VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B")
            };

            var accessMask = readOnly 
                ? VirtualDiskInterop.VirtualDiskAccessMask.AttachReadOnly
                : VirtualDiskInterop.VirtualDiskAccessMask.AttachReadWrite;

            var parameters = new VirtualDiskInterop.OpenVirtualDiskParametersV2
            {
                Version = VirtualDiskInterop.OpenVirtualDiskVersion.Version2,
                ReadOnly = readOnly,
                GetInfoOnly = false,
                ResiliencyGuid = Guid.Empty
            };

            uint result = VirtualDiskInterop.OpenVirtualDisk(
                ref storageType,
                path,
                accessMask,
                VirtualDiskInterop.OpenVirtualDiskFlags.None,
                ref parameters,
                out SafeFileHandle handle);

            if (result != 0)
                throw new InvalidOperationException($"Failed to open virtual disk: Error {result}");

            return (IntPtr)handle.DangerousGetHandle();
        });
    }

    public void CloseVhdx(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;

        var safeHandle = new SafeFileHandle(handle, true);
        safeHandle.Dispose();
    }

    public async Task<char> MountToDriveLetterAsync(string path, bool readOnly = true)
    {
        return await Task.Run(() =>
        {
            Log.Debug("MountToDriveLetter: path={Path}, readOnly={ReadOnly}", path, readOnly);
            
            var storageType = new VirtualDiskInterop.VirtualStorageTypeStruct
            {
                DeviceId = VirtualDiskInterop.VirtualStorageType.VHDX,
                VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B")
            };

            var openParams = new VirtualDiskInterop.OpenVirtualDiskParametersV2
            {
                Version = VirtualDiskInterop.OpenVirtualDiskVersion.Version2,
                ReadOnly = readOnly,
                GetInfoOnly = false,
                ResiliencyGuid = Guid.Empty
            };

            var accessMask = readOnly 
                ? VirtualDiskInterop.VirtualDiskAccessMask.AttachReadOnly
                : VirtualDiskInterop.VirtualDiskAccessMask.AttachReadWrite;

            uint result = VirtualDiskInterop.OpenVirtualDisk(
                ref storageType,
                path,
                accessMask,
                VirtualDiskInterop.OpenVirtualDiskFlags.None,
                ref openParams,
                out SafeFileHandle handle);

            int win32 = Marshal.GetLastWin32Error();
            if (result != 0)
            {
                Log.Error("OpenVirtualDisk (Mount) FAILED: result={Result}, Win32={Win32} (0x{Win32X}), path={Path}", result, win32, win32.ToString("X"), path);
                throw new InvalidOperationException($"Failed to open virtual disk for mounting: Error {result}");
            }

            try
            {
                // Find an available drive letter
                char driveLetter = FindAvailableDriveLetter();
                Log.Debug("Found available drive letter: {Letter}", driveLetter);

                var attachParams = new VirtualDiskInterop.AttachVirtualDiskParametersV1
                {
                    Version = 1,
                    Reserved = 0
                };

                var attachFlags = readOnly 
                    ? VirtualDiskInterop.AttachVirtualDiskFlags.ReadOnly
                    : VirtualDiskInterop.AttachVirtualDiskFlags.None;

                // Attach with automatic drive letter assignment
                result = VirtualDiskInterop.AttachVirtualDisk(
                    handle,
                    IntPtr.Zero,
                    attachFlags,
                    0,
                    ref attachParams,
                    IntPtr.Zero);

                win32 = Marshal.GetLastWin32Error();
                if (result != 0)
                {
                    Log.Error("AttachVirtualDisk (Mount) FAILED: result={Result}, Win32={Win32} (0x{Win32X})", result, win32, win32.ToString("X"));
                    throw new InvalidOperationException($"Failed to attach virtual disk: Error {result}");
                }

                Log.Information("Successfully mounted VHDX to drive letter: {Letter}", driveLetter);
                
                // Track the mounted disk so we can dismount it later
                lock (_lock)
                {
                    _mountedDisks[path] = handle;
                }
                
                return driveLetter;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        });
    }

    private static char FindAvailableDriveLetter()
    {
        // Get all currently used drive letters
        var usedDrives = DriveInfo.GetDrives().Select(d => char.ToUpper(d.Name[0])).ToHashSet();

        // Find first available letter starting from Z and going backwards
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!usedDrives.Contains(letter))
            {
                return letter;
            }
        }

        throw new InvalidOperationException("No available drive letters found");
    }

    public async Task MountToFolderAsync(string path, string mountPoint, bool readOnly = true)
    {
        // This would require Windows NTFS mount point APIs
        // Placeholder for now
        await Task.CompletedTask;
    }

    public async Task DismountAsync(string path)
    {
        await Task.Run(() =>
        {
            Log.Debug("DismountAsync: path={Path}", path);
            
            SafeFileHandle? handle = null;
            lock (_lock)
            {
                if (_mountedDisks.TryGetValue(path, out handle))
                {
                    _mountedDisks.Remove(path);
                }
            }

            if (handle != null && !handle.IsInvalid)
            {
                try
                {
                    Log.Debug("Calling DetachVirtualDisk for {Path}", path);
                    uint result = VirtualDiskInterop.DetachVirtualDisk(handle, 0, 0);
                    if (result != 0)
                    {
                        Log.Warning("DetachVirtualDisk failed: result={Result} for {Path}", result, path);
                    }
                    else
                    {
                        Log.Information("Successfully dismounted VHDX: {Path}", path);
                    }
                }
                finally
                {
                    handle.Dispose();
                }
            }
            else
            {
                Log.Warning("DismountAsync: No mounted disk found for path {Path}", path);
            }
        });
    }

    /// <summary>
    /// Dismounts all tracked VHDXs. Call this on application exit.
    /// </summary>
    public void DismountAll()
    {
        Log.Debug("DismountAll: dismounting {Count} VHDXs", _mountedDisks.Count);
        
        List<string> paths;
        lock (_lock)
        {
            paths = _mountedDisks.Keys.ToList();
        }

        foreach (var path in paths)
        {
            try
            {
                DismountAsync(path).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error dismounting VHDX: {Path}", path);
            }
        }
    }

    public async Task<IAttachedVhdx> AttachVhdxForWriteAsync(string path, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            Log.Debug("AttachVhdxForWrite: path={Path}", path);
            var storageType = new VirtualDiskInterop.VirtualStorageTypeStruct
            {
                DeviceId = VirtualDiskInterop.VirtualStorageType.VHDX,
                VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B")
            };

            var openParams = new VirtualDiskInterop.OpenVirtualDiskParametersV2
            {
                Version = VirtualDiskInterop.OpenVirtualDiskVersion.Version2,
                ReadOnly = false,
                GetInfoOnly = false,
                ResiliencyGuid = Guid.Empty
            };

            uint result = VirtualDiskInterop.OpenVirtualDisk(
                ref storageType,
                path,
                VirtualDiskInterop.VirtualDiskAccessMask.AttachReadWrite,
                VirtualDiskInterop.OpenVirtualDiskFlags.None,
                ref openParams,
                out SafeFileHandle handle);

            int win32 = Marshal.GetLastWin32Error();
            if (result != 0)
            {
                Log.Error("OpenVirtualDisk FAILED: result={Result}, Win32={Win32} (0x{Win32X}), path={Path}", result, win32, win32.ToString("X"), path);
                throw new InvalidOperationException($"Failed to open virtual disk: Error {result}");
            }
            Log.Debug("OpenVirtualDisk succeeded");

            try
            {
                var attachParams = new VirtualDiskInterop.AttachVirtualDiskParametersV1
                {
                    Version = 1,
                    Reserved = 0
                };

                result = VirtualDiskInterop.AttachVirtualDisk(
                    handle,
                    IntPtr.Zero,
                    VirtualDiskInterop.AttachVirtualDiskFlags.NoDriveLetter,
                    0,
                    ref attachParams,
                    IntPtr.Zero);

                win32 = Marshal.GetLastWin32Error();
                if (result != 0)
                {
                    Log.Error("AttachVirtualDisk FAILED: result={Result}, Win32={Win32} (0x{Win32X})", result, win32, win32.ToString("X"));
                    throw new InvalidOperationException($"Failed to attach virtual disk: Error {result}");
                }
                Log.Debug("AttachVirtualDisk succeeded");

                uint pathSize = 260 * 2; // 260 chars * 2 bytes for Unicode
                IntPtr pathBuffer = Marshal.AllocHGlobal((int)pathSize);
                try
                {
                    result = VirtualDiskInterop.GetVirtualDiskPhysicalPath(handle, ref pathSize, pathBuffer);
                    win32 = Marshal.GetLastWin32Error();
                    if (result != 0)
                    {
                        Log.Error("GetVirtualDiskPhysicalPath FAILED: result={Result}, Win32={Win32} (0x{Win32X})", result, win32, win32.ToString("X"));
                        throw new InvalidOperationException($"Failed to get virtual disk path: Error {result}");
                    }

                    string physicalPath = Marshal.PtrToStringUni(pathBuffer) ?? throw new InvalidOperationException("Failed to read virtual disk path");
                    physicalPath = physicalPath.TrimEnd('\0');
                    Log.Information("GetVirtualDiskPhysicalPath: {PhysicalPath}", physicalPath);
                    return new AttachedVhdx(handle, physicalPath);
                }
                finally
                {
                    Marshal.FreeHGlobal(pathBuffer);
                }
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }, cancellationToken);
    }

    public async Task<IAttachedVhdx> CreateAndAttachVhdxForWriteAsync(string path, long maxSizeBytes, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            Log.Debug("CreateAndAttachVhdxForWrite: path={Path}, maxSizeBytes={Size}", path, maxSizeBytes);
            var storageType = new VirtualDiskInterop.VirtualStorageTypeStruct
            {
                DeviceId = VirtualDiskInterop.VirtualStorageType.VHDX,
                VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B")
            };

            var parameters = new VirtualDiskInterop.CreateVirtualDiskParametersV2
            {
                Version = VirtualDiskInterop.CreateVirtualDiskVersion.Version2,
                UniqueId = Guid.NewGuid(),
                MaximumSize = (ulong)maxSizeBytes,
                BlockSizeInBytes = 32 * 1024 * 1024,
                SectorSizeInBytes = 512,
                PhysicalSectorSizeInBytes = 512,
                ParentPath = IntPtr.Zero,
                SourcePath = IntPtr.Zero
            };

            uint result = VirtualDiskInterop.CreateVirtualDisk(
                ref storageType,
                path,
                VirtualDiskInterop.VirtualDiskAccessMask.None,
                IntPtr.Zero,
                VirtualDiskInterop.CreateVirtualDiskFlags.None,
                0,
                ref parameters,
                IntPtr.Zero,
                out SafeFileHandle handle);

            int win32 = Marshal.GetLastWin32Error();
            if (result != 0)
            {
                Log.Error("CreateVirtualDisk FAILED: result={Result}, Win32={Win32} (0x{Win32X}), path={Path}", result, win32, win32.ToString("X"), path);
                throw new InvalidOperationException($"Failed to create virtual disk: Error {result}");
            }
            Log.Debug("CreateVirtualDisk succeeded, attaching without closing handle");

            try
            {
                var attachParams = new VirtualDiskInterop.AttachVirtualDiskParametersV1
                {
                    Version = 1,
                    Reserved = 0
                };

                result = VirtualDiskInterop.AttachVirtualDisk(
                    handle,
                    IntPtr.Zero,
                    VirtualDiskInterop.AttachVirtualDiskFlags.NoDriveLetter,
                    0,
                    ref attachParams,
                    IntPtr.Zero);

                win32 = Marshal.GetLastWin32Error();
                if (result != 0)
                {
                    Log.Error("AttachVirtualDisk FAILED: result={Result}, Win32={Win32} (0x{Win32X})", result, win32, win32.ToString("X"));
                    handle.Dispose();
                    throw new InvalidOperationException($"Failed to attach virtual disk: Error {result}");
                }
                Log.Debug("AttachVirtualDisk succeeded");

                uint pathSize = 260 * 2;
                IntPtr pathBuffer = Marshal.AllocHGlobal((int)pathSize);
                try
                {
                    result = VirtualDiskInterop.GetVirtualDiskPhysicalPath(handle, ref pathSize, pathBuffer);
                    win32 = Marshal.GetLastWin32Error();
                    if (result != 0)
                    {
                        Log.Error("GetVirtualDiskPhysicalPath FAILED: result={Result}, Win32={Win32} (0x{Win32X})", result, win32, win32.ToString("X"));
                        handle.Dispose();
                        throw new InvalidOperationException($"Failed to get virtual disk path: Error {result}");
                    }

                    string physicalPath = Marshal.PtrToStringUni(pathBuffer) ?? throw new InvalidOperationException("Failed to read virtual disk path");
                    physicalPath = physicalPath.TrimEnd('\0');
                    Log.Information("CreateAndAttach succeeded: PhysicalPath={PhysicalPath}", physicalPath);
                    return new AttachedVhdx(handle, physicalPath);
                }
                finally
                {
                    Marshal.FreeHGlobal(pathBuffer);
                }
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }, cancellationToken);
    }

    public async Task<IAttachedVhdx> AttachVhdxReadOnlyAsync(string path, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            Log.Debug("AttachVhdxReadOnly: path={Path}", path);
            var storageType = new VirtualDiskInterop.VirtualStorageTypeStruct
            {
                DeviceId = VirtualDiskInterop.VirtualStorageType.VHDX,
                VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B")
            };

            var openParams = new VirtualDiskInterop.OpenVirtualDiskParametersV2
            {
                Version = VirtualDiskInterop.OpenVirtualDiskVersion.Version2,
                ReadOnly = true,
                GetInfoOnly = false,
                ResiliencyGuid = Guid.Empty
            };

            uint result = VirtualDiskInterop.OpenVirtualDisk(
                ref storageType,
                path,
                VirtualDiskInterop.VirtualDiskAccessMask.AttachReadOnly,
                VirtualDiskInterop.OpenVirtualDiskFlags.None,
                ref openParams,
                out SafeFileHandle handle);

            int win32 = Marshal.GetLastWin32Error();
            if (result != 0)
            {
                Log.Error("OpenVirtualDisk (ReadOnly) FAILED: result={Result}, Win32={Win32} (0x{Win32X}), path={Path}", result, win32, win32.ToString("X"), path);
                throw new InvalidOperationException($"Failed to open virtual disk read-only: Error {result}");
            }
            Log.Debug("OpenVirtualDisk (ReadOnly) succeeded");

            try
            {
                var attachParams = new VirtualDiskInterop.AttachVirtualDiskParametersV1
                {
                    Version = 1,
                    Reserved = 0
                };

                result = VirtualDiskInterop.AttachVirtualDisk(
                    handle,
                    IntPtr.Zero,
                    VirtualDiskInterop.AttachVirtualDiskFlags.NoDriveLetter | VirtualDiskInterop.AttachVirtualDiskFlags.ReadOnly,
                    0,
                    ref attachParams,
                    IntPtr.Zero);

                win32 = Marshal.GetLastWin32Error();
                if (result != 0)
                {
                    Log.Error("AttachVirtualDisk (ReadOnly) FAILED: result={Result}, Win32={Win32} (0x{Win32X})", result, win32, win32.ToString("X"));
                    throw new InvalidOperationException($"Failed to attach virtual disk read-only: Error {result}");
                }
                Log.Debug("AttachVirtualDisk (ReadOnly) succeeded");

                uint pathSize = 260 * 2; // 260 chars * 2 bytes for Unicode
                IntPtr pathBuffer = Marshal.AllocHGlobal((int)pathSize);
                try
                {
                    result = VirtualDiskInterop.GetVirtualDiskPhysicalPath(handle, ref pathSize, pathBuffer);
                    win32 = Marshal.GetLastWin32Error();
                    if (result != 0)
                    {
                        Log.Error("GetVirtualDiskPhysicalPath (ReadOnly) FAILED: result={Result}, Win32={Win32} (0x{Win32X})", result, win32, win32.ToString("X"));
                        throw new InvalidOperationException($"Failed to get virtual disk path: Error {result}");
                    }

                    string physicalPath = Marshal.PtrToStringUni(pathBuffer) ?? throw new InvalidOperationException("Failed to read virtual disk path");
                    physicalPath = physicalPath.TrimEnd('\0');
                    Log.Information("GetVirtualDiskPhysicalPath (ReadOnly): {PhysicalPath}", physicalPath);
                    return new AttachedVhdx(handle, physicalPath);
                }
                finally
                {
                    Marshal.FreeHGlobal(pathBuffer);
                }
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }, cancellationToken);
    }
}
