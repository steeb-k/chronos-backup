using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Chronos.Native.Win32;
using Serilog;

namespace Chronos.Core.Disk;

/// <summary>
/// Service for reading raw disk sectors
/// </summary>
public interface IDiskReader
{
    /// <summary>
    /// Open a disk or partition for reading
    /// </summary>
    Task<DiskReadHandle> OpenDiskAsync(uint diskNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Open a partition for reading
    /// </summary>
    Task<DiskReadHandle> OpenPartitionAsync(uint diskNumber, uint partitionNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Open a path (e.g. volume \\.\C: or VSS snapshot) for sector reading.
    /// </summary>
    Task<DiskReadHandle> OpenPathForReadAsync(string path, ulong sizeBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read sectors from an open disk handle
    /// </summary>
    Task<int> ReadSectorsAsync(DiskReadHandle handle, byte[] buffer, long sectorOffset, int sectorCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the total size of the disk/partition in bytes
    /// </summary>
    Task<ulong> GetSizeAsync(DiskReadHandle handle, CancellationToken cancellationToken = default);
}

/// <summary>
/// Handle for an open disk
/// </summary>
public class DiskReadHandle : IDisposable
{
    public SafeFileHandle Handle { get; }
    public string Path { get; }
    public ulong SizeBytes { get; set; }
    public uint SectorSize { get; set; } = 512; // Default

    public DiskReadHandle(SafeFileHandle handle, string path)
    {
        Handle = handle ?? throw new ArgumentNullException(nameof(handle));
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public void Dispose()
    {
        Handle?.Dispose();
    }
}

/// <summary>
/// Implementation of disk reader using Windows APIs
/// </summary>
public class DiskReader : IDiskReader
{
    private const int DefaultBufferSize = 1024 * 1024; // 1 MB

    public async Task<DiskReadHandle> OpenDiskAsync(uint diskNumber, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            string diskPath = $"\\\\.\\PhysicalDrive{diskNumber}";
            Log.Debug("OpenDiskForRead: {Path}", diskPath);
            var handle = DiskApi.OpenDiskForRead(diskPath);
            int win32 = Marshal.GetLastWin32Error();

            if (handle.IsInvalid)
            {
                Log.Error("OpenDiskForRead FAILED: disk={Disk}, Win32={Win32} (0x{Win32X})", diskNumber, win32, win32.ToString("X"));
                throw new IOException($"Failed to open disk {diskNumber}. Error: {win32}");
            }

            var diskHandle = new DiskReadHandle(handle, diskPath);

            // Query disk size
            diskHandle.SizeBytes = QueryDiskSize(handle);

            return diskHandle;
        }, cancellationToken);
    }

    public async Task<DiskReadHandle> OpenPartitionAsync(uint diskNumber, uint partitionNumber, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            // Try various partition path formats
            string[] pathFormats = new[]
            {
                $"\\\\.\\Harddisk{diskNumber}Partition{partitionNumber}",
                $"\\\\.\\Disk{diskNumber}Partition{partitionNumber}"
            };

            SafeFileHandle? handle = null;
            string? successPath = null;

            foreach (var path in pathFormats)
            {
                handle = DiskApi.OpenDiskForRead(path);
                if (!handle.IsInvalid)
                {
                    successPath = path;
                    break;
                }
                handle?.Dispose();
            }

            if (handle == null || handle.IsInvalid || successPath == null)
                throw new IOException($"Failed to open partition Disk{diskNumber}Part{partitionNumber}. Error: {Marshal.GetLastWin32Error()}");

            var diskHandle = new DiskReadHandle(handle, successPath);
            diskHandle.SizeBytes = QueryDiskSize(handle);

            return diskHandle;
        }, cancellationToken);
    }

    public async Task<DiskReadHandle> OpenPathForReadAsync(string path, ulong sizeBytes, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var handle = DiskApi.OpenDiskForRead(path);
            if (handle.IsInvalid)
                throw new IOException($"Failed to open path {path}. Error: {Marshal.GetLastWin32Error()}");
            var h = new DiskReadHandle(handle, path);
            h.SizeBytes = sizeBytes;
            return h;
        }, cancellationToken);
    }

    public async Task<int> ReadSectorsAsync(DiskReadHandle handle, byte[] buffer, long sectorOffset, int sectorCount, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            long byteOffset = sectorOffset * handle.SectorSize;
            int bytesToRead = sectorCount * (int)handle.SectorSize;

            if (buffer.Length < bytesToRead)
                throw new ArgumentException($"Buffer too small. Need {bytesToRead} bytes, got {buffer.Length}");

            // Seek to position
            if (!DiskApi.SetFilePointerEx(handle.Handle, byteOffset, out _, DiskApi.FILE_BEGIN))
                throw new IOException($"Failed to seek to sector {sectorOffset}. Error: {Marshal.GetLastWin32Error()}");

            // Read data
            if (!DiskApi.ReadFile(handle.Handle, buffer, (uint)bytesToRead, out uint bytesRead, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to read sectors. Error: {error}");
            }

            return (int)bytesRead;
        }, cancellationToken);
    }

    public async Task<ulong> GetSizeAsync(DiskReadHandle handle, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => handle.SizeBytes, cancellationToken);
    }

    private ulong QueryDiskSize(SafeFileHandle handle)
    {
        // Allocate buffer for GET_LENGTH_INFO
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            if (DiskApi.DeviceIoControl(
                handle,
                DiskApi.IOCTL_DISK_GET_LENGTH_INFO,
                IntPtr.Zero,
                0,
                buffer,
                8,
                out _,
                IntPtr.Zero))
            {
                return (ulong)Marshal.ReadInt64(buffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // Fallback: return 0 if we can't get size
        return 0;
    }
}
