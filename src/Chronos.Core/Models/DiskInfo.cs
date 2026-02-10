using Chronos.Common.Extensions;

namespace Chronos.Core.Models;

public enum DiskPartitionStyle
{
    MBR = 0,
    GPT = 1,
    Unknown = 2
}

public class DiskInfo
{
    public uint DiskNumber { get; set; }
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public ulong Size { get; set; }
    public DiskPartitionStyle PartitionStyle { get; set; } = DiskPartitionStyle.Unknown;

    public override string ToString() =>
        $"{Manufacturer} {Model} ({((long)Size).ToHumanReadableSize()})";
}

public class PartitionInfo
{
    public uint DiskNumber { get; set; }
    public uint PartitionNumber { get; set; }
    public ulong Size { get; set; }
    public ulong Offset { get; set; }

    /// <summary>
    /// Volume path for FSCTL (e.g. "\\.\C:") if the partition has a drive letter. Null if not mounted.
    /// </summary>
    public string? VolumePath { get; set; }

    public override string ToString() =>
        $"Disk {DiskNumber}, Partition {PartitionNumber} ({((long)Size).ToHumanReadableSize()})";
}
