using Chronos.Core.Models;
using System.Linq;

namespace Chronos.Core.Services;

public interface IDiskEnumerator
{
    /// <summary>
    /// Enumerate all physical disks on the system.
    /// </summary>
    Task<List<DiskInfo>> GetDisksAsync();

    /// <summary>
    /// Enumerate all partitions on a specific disk.
    /// </summary>
    Task<List<PartitionInfo>> GetPartitionsAsync(uint diskNumber);

    /// <summary>
    /// Get a specific disk by number.
    /// </summary>
    Task<DiskInfo?> GetDiskAsync(uint diskNumber);

    /// <summary>
    /// Refresh the disk list (re-enumerate).
    /// </summary>
    Task RefreshAsync();
}

/// <summary>
/// Enumerates disks and partitions on Windows systems using WMI.
/// </summary>
public class DiskEnumerator : IDiskEnumerator
{
    private List<DiskInfo> _cachedDisks = new();
    private readonly Dictionary<uint, List<PartitionInfo>> _cachedPartitions = new();

    public async Task<List<DiskInfo>> GetDisksAsync()
    {
        if (_cachedDisks.Count > 0)
            return _cachedDisks;

        await RefreshAsync();
        return _cachedDisks;
    }

    public async Task<List<PartitionInfo>> GetPartitionsAsync(uint diskNumber)
    {
        // Check cache first
        if (_cachedPartitions.TryGetValue(diskNumber, out var cached))
            return cached;

        var partitions = await EnumeratePartitionsAsync(diskNumber);
        _cachedPartitions[diskNumber] = partitions;
        return partitions;
    }

    public async Task<DiskInfo?> GetDiskAsync(uint diskNumber)
    {
        var disks = await GetDisksAsync();
        return disks.FirstOrDefault(d => d.DiskNumber == diskNumber);
    }

    public async Task RefreshAsync()
    {
        _cachedDisks = await EnumerateDisksAsync();
        _cachedPartitions.Clear();
    }

    private async Task<List<DiskInfo>> EnumerateDisksAsync()
    {
        return await Task.Run(() =>
        {
            var disks = new List<DiskInfo>();

            try
            {
                // Use WMI to get physical disks
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT * FROM Win32_DiskDrive"))
                {
                    foreach (var disk in searcher.Get())
                    {
                        try
                        {
                            var diskInfo = new DiskInfo
                            {
                                DiskNumber = uint.Parse(disk["Index"].ToString() ?? "0"),
                                Manufacturer = disk["Manufacturer"]?.ToString() ?? "Unknown",
                                Model = disk["Model"]?.ToString() ?? "Unknown",
                                SerialNumber = disk["SerialNumber"]?.ToString() ?? "Unknown",
                                Size = ulong.TryParse(disk["Size"]?.ToString() ?? "0", out var size) ? size : 0,
                                PartitionStyle = DiskPartitionStyle.MBR, // Default; could be enhanced to detect GUID
                            };

                            disks.Add(diskInfo);
                        }
                        catch
                        {
                            // Skip disks that fail to enumerate
                        }
                    }
                }
            }
            catch
            {
                // If WMI fails, return empty list
            }

            return disks;
        });
    }

    private async Task<List<PartitionInfo>> EnumeratePartitionsAsync(uint diskNumber)
    {
        return await Task.Run(() =>
        {
            var partitions = new List<PartitionInfo>();

            try
            {
                // Use WMI to get partitions on this disk
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT * FROM Win32_DiskPartition WHERE DiskIndex = {diskNumber}"))
                {
                    foreach (var partition in searcher.Get())
                    {
                        try
                        {
                            var deviceId = partition["DeviceID"]?.ToString() ?? string.Empty;
                            // DeviceID "Disk #0, Partition #1" matches \\.\Harddisk0Partition1 (1-based)
                            var partitionNumber = ParsePartitionNumber(deviceId);
                            if (partitionNumber == 0)
                            {
                                var idx = TryGetUint(partition, "PartitionNumber") ?? TryGetUint(partition, "Index");
                                partitionNumber = idx.HasValue && idx.Value > 0 ? idx.Value : 1; // never use 0 for device path
                            }
                            var volumePath = GetVolumePathForPartition(partition);
                            var partInfo = new PartitionInfo
                            {
                                DiskNumber = diskNumber,
                                PartitionNumber = partitionNumber,
                                Size = ulong.TryParse(partition["Size"]?.ToString() ?? "0", out var size) ? size : 0,
                                Offset = ulong.TryParse(partition["StartingOffset"]?.ToString() ?? "0", out var offset) ? offset : 0,
                                VolumePath = volumePath,
                            };

                            partitions.Add(partInfo);
                        }
                        catch
                        {
                            // Skip partitions that fail to enumerate
                        }
                    }
                }
            }
            catch
            {
                // If WMI fails, return empty list
            }

            return partitions;
        });
    }

    private static uint? TryGetUint(System.Management.ManagementBaseObject obj, string propertyName)
    {
        try
        {
            var value = obj[propertyName]?.ToString();
            return uint.TryParse(value, out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static uint ParsePartitionNumber(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return 0;

        // Example: "Disk #0, Partition #1"
        var hashIndex = deviceId.LastIndexOf('#');
        if (hashIndex < 0 || hashIndex == deviceId.Length - 1)
            return 0;

        var digits = new string(deviceId.Skip(hashIndex + 1).TakeWhile(char.IsDigit).ToArray());
        return uint.TryParse(digits, out var value) ? value : 0;
    }

    /// <summary>
    /// Gets the volume path (e.g. \\.\C:) for a partition via Win32_LogicalDiskToPartition.
    /// Returns null if the partition has no drive letter.
    /// </summary>
    private static string? GetVolumePathForPartition(System.Management.ManagementBaseObject partition)
    {
        try
        {
            using var associator = new System.Management.ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{EscapeWmiString(partition["DeviceID"]?.ToString() ?? "")}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (var logicalDisk in associator.Get())
            {
                var deviceId = logicalDisk["DeviceID"]?.ToString();
                if (!string.IsNullOrEmpty(deviceId) && deviceId.Length >= 2 && deviceId[1] == ':')
                {
                    // e.g. "C:" -> "\\.\C:"
                    return $"\\\\.\\{deviceId}";
                }
            }
        }
        catch
        {
            // Ignore - partition may not have a drive letter
        }

        return null;
    }

    private static string EscapeWmiString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }
}
