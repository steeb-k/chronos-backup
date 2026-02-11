using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Chronos.Core.Models;

/// <summary>
/// Sidecar file stored alongside a backup image (.vhdx.chronos.json).
/// Contains disk and partition metadata for display in the restore UI
/// without needing to mount the image.
/// </summary>
public class ImageSidecar
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Chronos version that created the sidecar.</summary>
    public string ChronosVersion { get; set; } = Chronos.Common.Constants.AppConstants.Version;

    /// <summary>When the backup was created.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Partition style of the source disk.</summary>
    public string PartitionStyle { get; set; } = "Unknown";

    /// <summary>Source disk model name.</summary>
    public string? DiskModel { get; set; }

    /// <summary>Source disk serial number.</summary>
    public string? DiskSerial { get; set; }

    /// <summary>Total disk size in bytes.</summary>
    public ulong DiskSizeBytes { get; set; }

    /// <summary>Source disk number at time of backup.</summary>
    public uint SourceDiskNumber { get; set; }

    /// <summary>Partition entries from the source disk.</summary>
    public List<SidecarPartition> Partitions { get; set; } = new();

    /// <summary>
    /// Builds a sidecar from a DiskInfo and its partitions.
    /// </summary>
    public static ImageSidecar FromDisk(DiskInfo disk, IEnumerable<PartitionInfo> partitions)
    {
        var sidecar = new ImageSidecar
        {
            PartitionStyle = disk.PartitionStyle.ToString(),
            DiskModel = disk.Model,
            DiskSerial = disk.SerialNumber,
            DiskSizeBytes = disk.Size,
            SourceDiskNumber = disk.DiskNumber,
        };

        foreach (var p in partitions)
        {
            sidecar.Partitions.Add(new SidecarPartition
            {
                PartitionNumber = p.PartitionNumber,
                Size = p.Size,
                Offset = p.Offset,
                DriveLetter = p.DriveLetter,
                VolumeLabel = p.VolumeLabel,
                FileSystem = p.FileSystem,
                PartitionType = p.PartitionType,
                UsedSpace = p.UsedSpace,
                FreeSpace = p.FreeSpace,
            });
        }

        return sidecar;
    }

    /// <summary>
    /// Converts sidecar data back into DiskInfo + PartitionInfo for DiskMapControl display.
    /// </summary>
    public (DiskInfo Disk, List<PartitionInfo> Partitions) ToDiskAndPartitions()
    {
        var style = PartitionStyle switch
        {
            "GPT" => DiskPartitionStyle.GPT,
            "MBR" => DiskPartitionStyle.MBR,
            _ => DiskPartitionStyle.Unknown,
        };

        var disk = new DiskInfo
        {
            DiskNumber = SourceDiskNumber,
            Model = DiskModel ?? "Unknown",
            SerialNumber = DiskSerial ?? "Unknown",
            Size = DiskSizeBytes,
            PartitionStyle = style,
        };

        var parts = Partitions.Select(p => new PartitionInfo
        {
            DiskNumber = SourceDiskNumber,
            PartitionNumber = p.PartitionNumber,
            Size = p.Size,
            Offset = p.Offset,
            DriveLetter = p.DriveLetter,
            VolumeLabel = p.VolumeLabel,
            FileSystem = p.FileSystem,
            PartitionType = p.PartitionType,
            UsedSpace = p.UsedSpace,
            FreeSpace = p.FreeSpace,
        }).ToList();

        return (disk, parts);
    }

    /// <summary>
    /// Returns the sidecar file path for a given image path.
    /// e.g. "C:\backups\my-image.vhdx" → "C:\backups\my-image.vhdx.chronos.json"
    /// </summary>
    public static string GetSidecarPath(string imagePath) => imagePath + ".chronos.json";

    /// <summary>Saves the sidecar to disk.</summary>
    public async Task SaveAsync(string imagePath)
    {
        var path = GetSidecarPath(imagePath);
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            Log.Information("Saved image sidecar: {Path} ({Count} partitions)", path, Partitions.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save image sidecar: {Path}", path);
        }
    }

    /// <summary>Loads a sidecar from disk. Returns null if not found or unreadable.</summary>
    public static async Task<ImageSidecar?> LoadAsync(string imagePath)
    {
        var path = GetSidecarPath(imagePath);
        try
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var sidecar = JsonSerializer.Deserialize<ImageSidecar>(json, JsonOptions);
            Log.Debug("Loaded image sidecar: {Path} ({Count} partitions)", path, sidecar?.Partitions.Count ?? 0);
            return sidecar;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load image sidecar: {Path}", path);
            return null;
        }
    }
}

/// <summary>
/// Partition entry in a sidecar file.
/// </summary>
public class SidecarPartition
{
    public uint PartitionNumber { get; set; }
    public ulong Size { get; set; }
    public ulong Offset { get; set; }
    public string? DriveLetter { get; set; }
    public string? VolumeLabel { get; set; }
    public string? FileSystem { get; set; }
    public string? PartitionType { get; set; }
    public ulong? UsedSpace { get; set; }
    public ulong? FreeSpace { get; set; }
}
