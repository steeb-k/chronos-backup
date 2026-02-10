using System.Runtime.InteropServices;

namespace Chronos.Native.Structures;

/// <summary>
/// Native structures for Windows API calls.
/// </summary>
public static class NativeStructures
{
    /// <summary>
    /// DISK_GEOMETRY structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_GEOMETRY
    {
        public long Cylinders;
        public uint MediaType;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
    }

    /// <summary>
    /// DISK_GEOMETRY_EX structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_GEOMETRY_EX
    {
        public DISK_GEOMETRY Geometry;
        public long DiskSize;
        public byte Data;
    }

    /// <summary>
    /// PARTITION_INFORMATION_EX structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PARTITION_INFORMATION_EX
    {
        public uint PartitionStyle;
        public long StartingOffset;
        public long PartitionLength;
        public uint PartitionNumber;
        public bool RewritePartition;
        public Guid PartitionId;
    }

    /// <summary>
    /// STORAGE_DEVICE_NUMBER structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_DEVICE_NUMBER
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    /// <summary>
    /// VOLUME_DISK_EXTENTS structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VOLUME_DISK_EXTENTS
    {
        public uint NumberOfDiskExtents;
        public DISK_EXTENT Extents;
    }

    /// <summary>
    /// DISK_EXTENT structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_EXTENT
    {
        public uint DiskNumber;
        public long StartingOffset;
        public long ExtentLength;
    }
}
