using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Chronos.Native.Win32;
using Serilog;

namespace Chronos.Core.Disk;

/// <summary>
/// Interface for writing raw sectors to a disk or partition.
/// </summary>
public interface IDiskWriter
{
    /// <summary>
    /// Open a disk for writing.
    /// </summary>
    Task<DiskWriteHandle> OpenDiskForWriteAsync(string diskPath, CancellationToken cancellationToken = default);

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
            var handle = DiskApi.OpenDiskForWrite(diskPath);
            int win32 = Marshal.GetLastWin32Error();

            if (handle.IsInvalid)
            {
                Log.Error("OpenDiskForWrite FAILED: path={Path}, Win32={Win32} (0x{Win32X})", diskPath, win32, win32.ToString("X"));
                throw new IOException($"Failed to open disk for write: {diskPath}. Error: {win32}");
            }

            return new DiskWriteHandle(handle, diskPath);
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
                throw new IOException($"Failed to write sectors. Error: {error}");
            }

            if (bytesWritten != bytesToWrite)
                throw new IOException($"Partial write: expected {bytesToWrite}, wrote {bytesWritten}");
        }, cancellationToken);
    }
}
