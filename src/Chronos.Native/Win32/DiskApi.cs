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
    public static List<DriveLayoutEntry> GetDriveLayout(uint diskNumber)
    {
        var result = new List<DriveLayoutEntry>();
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

            // Header offset 0: PartitionStyle (0 = MBR, 1 = GPT)
            int partitionStyle = Marshal.ReadInt32(buffer, 0);
            int partitionCount = Marshal.ReadInt32(buffer, 4);

            for (int i = 0; i < partitionCount && i < maxPartitions; i++)
            {
                int offset = headerSize + (i * partEntrySize);
                long startingOffset = Marshal.ReadInt64(buffer, offset + 8);
                long partitionLength = Marshal.ReadInt64(buffer, offset + 16);
                uint partitionNumber = (uint)Marshal.ReadInt32(buffer, offset + 24);

                // PartitionNumber=0 means unused/empty entry, skip
                if (partitionLength <= 0 || partitionNumber <= 0)
                    continue;

                Guid gptTypeGuid = Guid.Empty;
                if (partitionStyle == 1) // GPT
                {
                    // PARTITION_INFORMATION_GPT starts at offset+32 within the entry
                    // PartitionType GUID is the first 16 bytes of the GPT union
                    byte[] guidBytes = new byte[16];
                    Marshal.Copy(buffer + offset + 32, guidBytes, 0, 16);
                    gptTypeGuid = new Guid(guidBytes);
                }

                result.Add(new DriveLayoutEntry
                {
                    PartitionNumber = partitionNumber,
                    StartingOffset = startingOffset,
                    PartitionLength = partitionLength,
                    GptTypeGuid = gptTypeGuid,
                });
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    /// <summary>
    /// Represents a partition entry from IOCTL_DISK_GET_DRIVE_LAYOUT_EX.
    /// </summary>
    public struct DriveLayoutEntry
    {
        public uint PartitionNumber;
        public long StartingOffset;
        public long PartitionLength;
        /// <summary>GPT partition type GUID (Guid.Empty for MBR disks).</summary>
        public Guid GptTypeGuid;
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

    /// <summary>
    /// Gets disk geometry (size, bytes per sector, etc.) via IOCTL_DISK_GET_DRIVE_GEOMETRY_EX.
    /// Returns null if the disk cannot be opened.
    /// </summary>
    public static DiskGeometryInfo? GetDiskGeometry(uint diskNumber)
    {
        string diskPath = $"\\\\.\\PhysicalDrive{diskNumber}";
        using var handle = OpenDiskForRead(diskPath);
        if (handle.IsInvalid)
            return null;

        // DISK_GEOMETRY_EX: DISK_GEOMETRY(24 bytes) + DiskSize(8) = 32 bytes minimum
        int bufferSize = 256;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bool ok = DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                IntPtr.Zero, 0, buffer, (uint)bufferSize, out _, IntPtr.Zero);
            if (!ok)
                return null;

            // DISK_GEOMETRY: Cylinders(8) + MediaType(4) + TracksPerCylinder(4) + SectorsPerTrack(4) + BytesPerSector(4) = 24
            long cylinders = Marshal.ReadInt64(buffer, 0);
            int mediaType = Marshal.ReadInt32(buffer, 8);
            int tracksPerCylinder = Marshal.ReadInt32(buffer, 12);
            int sectorsPerTrack = Marshal.ReadInt32(buffer, 16);
            int bytesPerSector = Marshal.ReadInt32(buffer, 20);
            long diskSize = Marshal.ReadInt64(buffer, 24);

            return new DiskGeometryInfo
            {
                DiskSize = diskSize,
                BytesPerSector = (uint)bytesPerSector,
                MediaType = mediaType,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Probes physical disk indices 0..maxIndex and returns which ones exist
    /// (can be opened). This is a pure IOCTL approach that works without WMI.
    /// </summary>
    public static List<uint> ProbePhysicalDiskIndices(uint maxIndex = 31)
    {
        var result = new List<uint>();
        for (uint i = 0; i <= maxIndex; i++)
        {
            string diskPath = $"\\\\.\\PhysicalDrive{i}";
            using var handle = CreateFile(diskPath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (!handle.IsInvalid)
                result.Add(i);
        }
        return result;
    }

    /// <summary>
    /// Information from IOCTL_DISK_GET_DRIVE_GEOMETRY_EX.
    /// </summary>
    public struct DiskGeometryInfo
    {
        public long DiskSize;
        public uint BytesPerSector;
        /// <summary>MEDIA_TYPE enum value (11 = FixedMedia, 12 = RemovableMedia).</summary>
        public int MediaType;
    }
}
