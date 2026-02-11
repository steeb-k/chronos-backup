using System.Runtime.InteropServices;
using System.Text;
using Chronos.Native.Structures;
using Microsoft.Win32.SafeHandles;

namespace Chronos.Native.Win32;

/// <summary>
/// P/Invoke declarations for volume and filesystem operations.
/// </summary>
public static partial class VolumeApi
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    // FSCTL_QUERY_ALLOCATED_RANGES - file-level; for sparse/compressed files only
    public const uint FSCTL_QUERY_ALLOCATED_RANGES = 0x000940CF;

    // FSCTL_GET_VOLUME_BITMAP - volume-level; returns bitmap of allocated clusters (correct for volume backup)
    public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;

    // FSCTL_GET_NTFS_VOLUME_DATA - returns cluster size; works on device paths (VSS snapshots)
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

    [StructLayout(LayoutKind.Sequential)]
    public struct NtfsVolumeDataBuffer
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartingLcnInputBuffer
    {
        public long StartingLcn;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VolumeBitmapBufferHeader
    {
        public long StartingLcn;
        public long BitmapSize; // number of clusters in bitmap
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileAllocatedRangeBuffer
    {
        public long FileOffset;
        public long Length;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

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

    /// <summary>
    /// Opens a partition/volume for read-only access (for querying allocated ranges).
    /// </summary>
    public static SafeFileHandle OpenPartitionForRead(string partitionPath)
    {
        return CreateFile(
            partitionPath,
            GENERIC_READ,
            FILE_SHARE_READ,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);
    }

    /// <summary>
    /// Opens a volume by drive letter (e.g. \\.\C:) or device path (e.g. VSS snapshot) for read-only access.
    /// Uses FILE_SHARE_READ | FILE_SHARE_WRITE to allow opening live system volumes when other processes have them open.
    /// </summary>
    public static SafeFileHandle OpenVolumeForRead(string volumePath)
    {
        return CreateFile(
            volumePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);
    }

    // --- Volume GUID enumeration ---

    [DllImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstVolumeNative(StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolumeNative(IntPtr hFindVolume, StringBuilder lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr hFindVolume);

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    /// <summary>
    /// Enumerates all volume GUID paths on the system (e.g. \\?\Volume{GUID}\).
    /// </summary>
    public static List<string> EnumerateVolumeGuids()
    {
        var volumes = new List<string>();
        var sb = new StringBuilder(260);

        var handle = FindFirstVolumeNative(sb, (uint)sb.Capacity);
        if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE)
            return volumes;

        try
        {
            do
            {
                volumes.Add(sb.ToString());
                sb.Clear();
            }
            while (FindNextVolumeNative(handle, sb, (uint)sb.Capacity));
        }
        finally
        {
            FindVolumeClose(handle);
        }

        return volumes;
    }

    /// <summary>
    /// Gets the disk extents for a volume (which disk(s) and offset(s) it spans).
    /// Returns null if the query fails (e.g. the volume is not on a physical disk).
    /// </summary>
    public static NativeStructures.DISK_EXTENT? GetVolumeDiskExtent(string volumeDevicePath)
    {
        using var handle = OpenVolumeForRead(volumeDevicePath);
        if (handle.IsInvalid)
            return null;

        // Allocate buffer for VOLUME_DISK_EXTENTS with one extent
        int bufferSize = Marshal.SizeOf<NativeStructures.VOLUME_DISK_EXTENTS>();
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            bool ok = DeviceIoControl(
                handle,
                IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                IntPtr.Zero,
                0,
                buffer,
                (uint)bufferSize,
                out _,
                IntPtr.Zero);

            if (!ok)
                return null;

            var extents = Marshal.PtrToStructure<NativeStructures.VOLUME_DISK_EXTENTS>(buffer);
            if (extents.NumberOfDiskExtents == 0)
                return null;

            return extents.Extents;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Strips the trailing backslash from a volume GUID path for use with CreateFile.
    /// e.g. \\?\Volume{GUID}\ → \\?\Volume{GUID}
    /// </summary>
    public static string VolumeGuidToDevicePath(string volumeGuid)
    {
        return volumeGuid.TrimEnd('\\');
    }
}
