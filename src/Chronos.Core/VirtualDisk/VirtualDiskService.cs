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
        // This is a simplified implementation
        // In a real scenario, you'd need to use Windows APIs to find an available drive letter
        // and use SetupDiXxx functions to mount the disk
        return await Task.Run(() =>
        {
            // For now, just return Z: as a placeholder
            // This would need proper implementation
            return 'Z';
        });
    }

    public async Task MountToFolderAsync(string path, string mountPoint, bool readOnly = true)
    {
        // This would require Windows NTFS mount point APIs
        // Placeholder for now
        await Task.CompletedTask;
    }

    public async Task DismountAsync(string path)
    {
        // This would require proper detach logic
        // Placeholder for now
        await Task.CompletedTask;
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
}
