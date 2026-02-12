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
    public bool IsSystemDisk { get; set; }
    public bool IsBootDisk { get; set; }

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

    /// <summary>Drive letter with colon, e.g. "C:". Null if no letter assigned.</summary>
    public string? DriveLetter { get; set; }

    /// <summary>Volume label, e.g. "Local Disk", "EFI", "Recovery". Null if unavailable.</summary>
    public string? VolumeLabel { get; set; }

    /// <summary>Filesystem type, e.g. "NTFS", "FAT32", "exFAT". Null if unknown.</summary>
    public string? FileSystem { get; set; }

    /// <summary>Partition type description, e.g. "EFI (ESP)", "Primary", "Recovery". Null if unknown.</summary>
    public string? PartitionType { get; set; }

    /// <summary>Raw GPT type GUID. Null for MBR partitions or when unavailable.</summary>
    public Guid? GptTypeGuid { get; set; }

    /// <summary>True when this entry represents an unallocated (free) region on the disk rather than a real partition.</summary>
    public bool IsUnallocated { get; set; }

    /// <summary>Used space in bytes, if available. Null if unknown.</summary>
    public ulong? UsedSpace { get; set; }

    /// <summary>Free space in bytes, if available. Null if unknown.</summary>
    public ulong? FreeSpace { get; set; }

    /// <summary>Usage ratio (0.0 – 1.0). Null if used/free not known.</summary>
    public double? UsageRatio => UsedSpace.HasValue && Size > 0 ? (double)UsedSpace.Value / Size : null;

    /// <summary>
    /// Short display label combining type, label and letter.
    /// e.g. "EFI (ESP)", "Primary (Local Disk, C:)", "Recovery"
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            if (IsUnallocated)
                return $"Unallocated — {((long)Size).ToHumanReadableSize()}";
            var type = PartitionType ?? "Partition";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(VolumeLabel))
                parts.Add(VolumeLabel);
            if (!string.IsNullOrEmpty(DriveLetter))
                parts.Add(DriveLetter);
            return parts.Count > 0 ? $"{type} ({string.Join(", ", parts)})" : type;
        }
    }

    public override string ToString() =>
        IsUnallocated
            ? $"Unallocated — {((long)Size).ToHumanReadableSize()}"
            : DiskNumber == uint.MaxValue
                ? PartitionType ?? "Entire Disk"
                : $"{DisplayLabel} — {((long)Size).ToHumanReadableSize()}";
}
