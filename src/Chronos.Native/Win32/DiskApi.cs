using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Chronos.Native.Win32;

/// <summary>
/// P/Invoke declarations for disk operations.
/// </summary>
public static partial class DiskApi
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_GET_PARTITION_INFO_EX = 0x00070048;
    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    public const uint IOCTL_STORAGE_GET_HOTPLUG_INFO = 0x002D0C14;
    public const uint IOCTL_DISK_SET_DISK_ATTRIBUTES = 0x0007C0F4;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;

    // Disk attribute flags for IOCTL_DISK_SET_DISK_ATTRIBUTES
    public const ulong DISK_ATTRIBUTE_OFFLINE = 0x01;
    public const ulong DISK_ATTRIBUTE_READ_ONLY = 0x02;
    
    public const uint FILE_BEGIN = 0;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    /// <summary>
    /// Opens a disk for reading.
    /// </summary>
    /// <param name="diskPath">The disk path (e.g., \\.\PhysicalDrive0).</param>
    /// <returns>A safe file handle to the disk.</returns>
    public static SafeFileHandle OpenDiskForRead(string diskPath)
    {
        return CreateFile(
            diskPath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);
    }

    /// <summary>
    /// Queries the drive layout via IOCTL_DISK_GET_DRIVE_LAYOUT_EX and returns
    /// a list of (PartitionNumber, StartingOffset, PartitionLength) for all partitions.
    /// The PartitionNumber here matches the \\.\\.\Harddisk{N}Partition{M} device path.
    /// </summary>
    public static List<(uint PartitionNumber, long StartingOffset, long PartitionLength)> GetDriveLayout(uint diskNumber)
    {
        var result = new List<(uint, long, long)>();
        string diskPath = $"\\\\.\\PhysicalDrive{diskNumber}";

        using var handle = OpenDiskForRead(diskPath);
        if (handle.IsInvalid)
            return result;

        // Header: PartitionStyle(4) + PartitionCount(4) + Union(40) = 48 bytes
        // Each PARTITION_INFORMATION_EX = 144 bytes on x64
        const int headerSize = 48;
        const int partEntrySize = 144;
        const int maxPartitions = 128;
        int bufferSize = headerSize + (partEntrySize * maxPartitions);

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bool ok = DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_LAYOUT_EX,
                IntPtr.Zero, 0, buffer, (uint)bufferSize, out _, IntPtr.Zero);
            if (!ok)
                return result;

            int partitionCount = Marshal.ReadInt32(buffer, 4);

            for (int i = 0; i < partitionCount && i < maxPartitions; i++)
            {
                int offset = headerSize + (i * partEntrySize);
                long startingOffset = Marshal.ReadInt64(buffer, offset + 8);
                long partitionLength = Marshal.ReadInt64(buffer, offset + 16);
                uint partitionNumber = (uint)Marshal.ReadInt32(buffer, offset + 24);

                // PartitionNumber=0 means unused/empty entry, skip
                if (partitionLength > 0 && partitionNumber > 0)
                    result.Add((partitionNumber, startingOffset, partitionLength));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    /// <summary>
    /// Opens a disk for writing.
    /// </summary>
    /// <param name="diskPath">The disk path (e.g., \\.\PhysicalDrive0).</param>
    /// <returns>A safe file handle to the disk.</returns>
    public static SafeFileHandle OpenDiskForWrite(string diskPath)
    {
        return CreateFile(
            diskPath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);
    }
}
