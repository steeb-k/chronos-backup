using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Chronos.Native.Win32;
using Chronos.Native.Structures;
using Serilog;

namespace Chronos.Core.Disk;

/// <summary>
/// Interface for writing raw sectors to a disk or partition.
/// </summary>
public interface IDiskWriter
{
    /// <summary>
    /// Open a disk for writing. Locks and dismounts the disk before returning.
    /// </summary>
    Task<DiskWriteHandle> OpenDiskForWriteAsync(string diskPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Open a partition for writing.
    /// </summary>
    Task<DiskWriteHandle> OpenPartitionForWriteAsync(uint diskNumber, uint partitionNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write sectors to an open disk handle.
    /// </summary>
    Task WriteSectorsAsync(DiskWriteHandle handle, byte[] buffer, long sectorOffset, int sectorCount, CancellationToken cancellationToken = default);
}

/// <summary>
/// Handle for an open disk being written to.
/// </summary>
public class DiskWriteHandle : IDisposable
{
    public SafeFileHandle Handle { get; }
    public string Path { get; }
    public uint SectorSize { get; set; } = 512;

    public DiskWriteHandle(SafeFileHandle handle, string path)
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
/// Implementation of disk writer using Windows APIs.
/// </summary>
public class DiskWriter : IDiskWriter
{
    public async Task<DiskWriteHandle> OpenDiskForWriteAsync(string diskPath, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            Log.Debug("OpenDiskForWrite: {Path}", diskPath);
            var handle = DiskApi.CreateFile(
                diskPath,
                0x80000000 | 0x40000000, // GENERIC_READ | GENERIC_WRITE
                0x00000001 | 0x00000002, // FILE_SHARE_READ | FILE_SHARE_WRITE
                IntPtr.Zero,
                3, // OPEN_EXISTING
                0x80, // FILE_ATTRIBUTE_NORMAL
                IntPtr.Zero);
            int win32 = Marshal.GetLastWin32Error();

            if (handle.IsInvalid)
            {
                Log.Error("OpenDiskForWrite FAILED: path={Path}, Win32={Win32} (0x{Win32X})", diskPath, win32, win32.ToString("X"));
                throw new IOException($"Failed to open disk for write: {diskPath}. Error: {win32}");
            }

            Log.Information("Successfully opened disk for write: {Path}", diskPath);
            var writeHandle = new DiskWriteHandle(handle, diskPath);
            writeHandle.SectorSize = QuerySectorSize(handle);
            Log.Debug("Disk write handle: Path={Path}, SectorSize={SectorSize}", diskPath, writeHandle.SectorSize);
            return writeHandle;
        }, cancellationToken);
    }

    public async Task<DiskWriteHandle> OpenPartitionForWriteAsync(uint diskNumber, uint partitionNumber, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            string partPath = $"\\\\.\\Harddisk{diskNumber}Partition{partitionNumber}";
            Log.Debug("OpenPartitionForWrite: {Path}", partPath);
            var handle = DiskApi.OpenDiskForWrite(partPath);
            int win32 = Marshal.GetLastWin32Error();

            if (handle.IsInvalid)
            {
                Log.Error("OpenPartitionForWrite FAILED: path={Path}, Win32={Win32} (0x{Win32X})", partPath, win32, win32.ToString("X"));
                throw new IOException($"Failed to open partition for write: {partPath}. Error: {win32}");
            }

            var writeHandle = new DiskWriteHandle(handle, partPath);
            writeHandle.SectorSize = QuerySectorSize(handle);
            Log.Debug("Partition write handle: Path={Path}, SectorSize={SectorSize}", partPath, writeHandle.SectorSize);
            return writeHandle;
        }, cancellationToken);
    }

    public async Task WriteSectorsAsync(DiskWriteHandle handle, byte[] buffer, long sectorOffset, int sectorCount, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            long byteOffset = sectorOffset * handle.SectorSize;
            int bytesToWrite = sectorCount * (int)handle.SectorSize;

            if (buffer.Length < bytesToWrite)
                throw new ArgumentException($"Buffer too small. Need {bytesToWrite} bytes, got {buffer.Length}");

            if (!DiskApi.SetFilePointerEx(handle.Handle, byteOffset, out _, DiskApi.FILE_BEGIN))
                throw new IOException($"Failed to seek to sector {sectorOffset}. Error: {Marshal.GetLastWin32Error()}");

            if (!DiskApi.WriteFile(handle.Handle, buffer, (uint)bytesToWrite, out uint bytesWritten, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                string errorMsg = GetFriendlyErrorMessage(error, "write to disk");
                throw new IOException(errorMsg);
            }

            if (bytesWritten != bytesToWrite)
                throw new IOException($"Partial write: expected {bytesToWrite}, wrote {bytesWritten}");
        }, cancellationToken);
    }

    private static string GetFriendlyErrorMessage(int errorCode, string operation)
    {
        return errorCode switch
        {
            5 => $"Access denied when trying to {operation}. The disk may be in use by Windows. Try:\n" +
                 "1. Close any programs accessing the disk\n" +
                 "2. Unmount any volumes on the target disk\n" +
                 "3. Take the disk offline in Disk Management (Win+X -> Disk Management)\n" +
                 "4. Ensure you are running as Administrator",
            19 => $"The disk is write-protected. The media may have a write-protection switch, or Windows is preventing writes " +
                  $"because volumes are still mounted. Ensure all volumes are dismounted and try again.",
            32 => $"The disk is locked by another process. Close any programs accessing the disk and try again.",
            33 => $"The disk is being used by another process. Close any programs accessing the disk and try again.",
            112 => $"Insufficient disk space to {operation}.",
            1 => $"Invalid function when trying to {operation}. The disk may not support this operation.",
            _ => $"Failed to {operation}. Windows error code: {errorCode}"
        };
    }

    private static uint QuerySectorSize(SafeFileHandle handle)
    {
        int bufferSize = Marshal.SizeOf<NativeStructures.DISK_GEOMETRY_EX>();
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (DiskApi.DeviceIoControl(
                handle,
                DiskApi.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                IntPtr.Zero,
                0,
                buffer,
                (uint)bufferSize,
                out _,
                IntPtr.Zero))
            {
                var geometry = Marshal.PtrToStructure<NativeStructures.DISK_GEOMETRY_EX>(buffer);
                uint sectorSize = geometry.Geometry.BytesPerSector;
                if (sectorSize > 0)
                    return sectorSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return 512; // Fallback
    }
}
