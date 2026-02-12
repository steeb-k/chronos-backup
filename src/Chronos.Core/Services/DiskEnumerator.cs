using Chronos.Core.Models;
using Chronos.Common.Helpers;
using Chronos.Native.Win32;
using Serilog;
using System.IO;
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

    /// <summary>
    /// Returns unallocated (free) regions on a disk as pseudo-PartitionInfo entries.
    /// Each entry has <see cref="PartitionInfo.IsUnallocated"/> = true.
    /// </summary>
    Task<List<PartitionInfo>> GetUnallocatedSpacesAsync(uint diskNumber);
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

    public async Task<List<PartitionInfo>> GetUnallocatedSpacesAsync(uint diskNumber)
    {
        var disk = await GetDiskAsync(diskNumber);
        if (disk is null)
            return new List<PartitionInfo>();

        var partitions = await GetPartitionsAsync(diskNumber);
        return ComputeUnallocatedSpaces(disk, partitions);
    }

    /// <summary>
    /// Computes unallocated (free) regions on a disk by finding gaps between partitions.
    /// </summary>
    internal static List<PartitionInfo> ComputeUnallocatedSpaces(DiskInfo disk, List<PartitionInfo> partitions)
    {
        var result = new List<PartitionInfo>();

        // Typical GPT overhead: first 1 MiB is reserved for protective MBR + primary GPT header/entries.
        // Last 1 MiB is reserved for backup GPT.
        const ulong gptFrontReserved = 1024 * 1024;      // 1 MiB
        const ulong gptBackReserved  = 1024 * 1024;      // 1 MiB
        const ulong minUsableGap     = 10 * 1024 * 1024;  // Ignore gaps < 10 MiB

        ulong usableStart = gptFrontReserved;
        ulong usableEnd   = disk.Size > gptBackReserved ? disk.Size - gptBackReserved : disk.Size;

        // Sort partitions by offset
        var sorted = partitions.OrderBy(p => p.Offset).ToList();

        ulong cursor = usableStart;
        uint unallocIndex = 0;

        foreach (var part in sorted)
        {
            if (part.Offset > cursor)
            {
                ulong gapSize = part.Offset - cursor;
                if (gapSize >= minUsableGap)
                {
                    unallocIndex++;
                    result.Add(new PartitionInfo
                    {
                        DiskNumber = disk.DiskNumber,
                        PartitionNumber = 10000 + unallocIndex, // high sentinel number
                        Size = gapSize,
                        Offset = cursor,
                        IsUnallocated = true,
                        PartitionType = "Unallocated",
                    });
                }
            }
            cursor = Math.Max(cursor, part.Offset + part.Size);
        }

        // Gap after the last partition
        if (cursor < usableEnd)
        {
            ulong gapSize = usableEnd - cursor;
            if (gapSize >= minUsableGap)
            {
                unallocIndex++;
                result.Add(new PartitionInfo
                {
                    DiskNumber = disk.DiskNumber,
                    PartitionNumber = 10000 + unallocIndex,
                    Size = gapSize,
                    Offset = cursor,
                    IsUnallocated = true,
                    PartitionType = "Unallocated",
                });
            }
        }

        return result;
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
                            var diskNumber = uint.Parse(disk["Index"].ToString() ?? "0");
                            var diskInfo = new DiskInfo
                            {
                                DiskNumber = diskNumber,
                                Manufacturer = disk["Manufacturer"]?.ToString() ?? "Unknown",
                                Model = disk["Model"]?.ToString() ?? "Unknown",
                                SerialNumber = disk["SerialNumber"]?.ToString() ?? "Unknown",
                                Size = ulong.TryParse(disk["Size"]?.ToString() ?? "0", out var size) ? size : 0,
                                PartitionStyle = GetPartitionStyle(diskNumber),
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
            catch (Exception wmiEx)
            {
                // WMI failed — fall back to IOCTL-only enumeration (critical for WinPE)
                Log.Warning(wmiEx, "WMI disk enumeration failed; falling back to IOCTL probing");
                disks = EnumerateDisksViaIoctl();
            }

            return disks;
        });
    }

    private async Task<List<PartitionInfo>> EnumeratePartitionsAsync(uint diskNumber)
    {
        return await Task.Run(() =>
        {
            var partitions = new List<PartitionInfo>();

            // First, get the real drive layout from IOCTL to know exact device partition numbers.
            // WMI may skip certain partition types (MSR, EFI) making its index unreliable.
            var driveLayout = Chronos.Native.Win32.DiskApi.GetDriveLayout(diskNumber);
            Log.Debug("Drive layout for disk {Disk}: {Count} partitions from IOCTL", diskNumber, driveLayout.Count);

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
                            var wmiOffset = long.TryParse(partition["StartingOffset"]?.ToString() ?? "0", out var off) ? off : 0L;

                            // Match this WMI partition to the IOCTL drive layout by starting offset.
                            // This is the only reliable way since WMI may skip partitions (MSR, etc.)
                            // and its index doesn't match \\.\Harddisk{N}Partition{M} numbering.
                            uint partitionNumber = 0;
                            var layoutMatch = driveLayout.FirstOrDefault(e => e.StartingOffset == wmiOffset);
                            if (layoutMatch.PartitionNumber > 0)
                            {
                                partitionNumber = layoutMatch.PartitionNumber;
                            }
                            else
                            {
                                // Fallback: WMI index + 1 (may be wrong if hidden partitions exist)
                                var wmIndex = ParsePartitionNumber(deviceId);
                                partitionNumber = wmIndex != uint.MaxValue ? wmIndex + 1 : 1;
                                Log.Warning("Could not match WMI partition {DeviceID} (offset {Offset}) to drive layout; using fallback partition number {Num}",
                                    deviceId, wmiOffset, partitionNumber);
                            }

                            var (volumePath, driveLetter) = GetVolumePathAndLetterForPartition(partition);
                            var partType = ClassifyPartitionType(partition);
                            var partInfo = new PartitionInfo
                            {
                                DiskNumber = diskNumber,
                                PartitionNumber = partitionNumber,
                                Size = ulong.TryParse(partition["Size"]?.ToString() ?? "0", out var size) ? size : 0,
                                Offset = (ulong)wmiOffset,
                                VolumePath = volumePath,
                                DriveLetter = driveLetter,
                                PartitionType = partType,
                                GptTypeGuid = layoutMatch.PartitionNumber > 0 && layoutMatch.GptTypeGuid != Guid.Empty
                                    ? layoutMatch.GptTypeGuid : null,
                            };

                            Log.Debug("Partition enumerated: WMI DeviceID={DeviceID}, Offset={Offset} → DevicePartition={PartNum}, Size={Size}, Type={Type}",
                                deviceId, wmiOffset, partitionNumber, partInfo.Size, partType);

                            partitions.Add(partInfo);
                        }
                        catch
                        {
                            // Skip partitions that fail to enumerate
                        }
                    }
                }
            }
            catch (Exception wmiEx)
            {
                // WMI partition enumeration failed — build from IOCTL drive layout only (WinPE fallback)
                Log.Warning(wmiEx, "WMI partition enumeration failed for disk {Disk}; using IOCTL-only", diskNumber);
                foreach (var entry in driveLayout)
                {
                    var partType = ClassifyGptTypeGuid(entry.GptTypeGuid);
                    partitions.Add(new PartitionInfo
                    {
                        DiskNumber = diskNumber,
                        PartitionNumber = entry.PartitionNumber,
                        Size = (ulong)entry.PartitionLength,
                        Offset = (ulong)entry.StartingOffset,
                        PartitionType = partType,
                        GptTypeGuid = entry.GptTypeGuid != Guid.Empty ? entry.GptTypeGuid : null,
                    });
                }

                // Resolve volume GUID paths and enrich
                ResolveVolumeGuidPaths(diskNumber, partitions);
                EnrichPartitionsWithVolumeMetadata(partitions);
                return partitions;
            }

            // Add partitions that IOCTL found but WMI skipped (e.g. MSR partitions).
            // This prevents them from appearing as false "unallocated space".
            foreach (var entry in driveLayout)
            {
                if (partitions.Any(p => p.PartitionNumber == entry.PartitionNumber))
                    continue;

                var partType = ClassifyGptTypeGuid(entry.GptTypeGuid);
                var ioctlPart = new PartitionInfo
                {
                    DiskNumber = diskNumber,
                    PartitionNumber = entry.PartitionNumber,
                    Size = (ulong)entry.PartitionLength,
                    Offset = (ulong)entry.StartingOffset,
                    PartitionType = partType,
                    GptTypeGuid = entry.GptTypeGuid != Guid.Empty ? entry.GptTypeGuid : null,
                };

                Log.Debug("Partition from IOCTL only (WMI skipped): Disk={Disk}, Partition={PartNum}, Offset={Offset}, Size={Size}, Type={Type}",
                    diskNumber, entry.PartitionNumber, entry.StartingOffset, entry.PartitionLength, partType);

                partitions.Add(ioctlPart);
            }

            // Resolve volume GUID paths for partitions that don't have drive letters
            ResolveVolumeGuidPaths(diskNumber, partitions);

            // Enrich each partition that has a volume path with label, filesystem, used/free
            EnrichPartitionsWithVolumeMetadata(partitions);

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
            return uint.MaxValue;

        // Example: "Disk #0, Partition #1" — the number after last '#' is the 0-based WMI index
        var hashIndex = deviceId.LastIndexOf('#');
        if (hashIndex < 0 || hashIndex == deviceId.Length - 1)
            return uint.MaxValue;

        var digits = new string(deviceId.Skip(hashIndex + 1).TakeWhile(char.IsDigit).ToArray());
        return uint.TryParse(digits, out var value) ? value : uint.MaxValue;
    }

    /// <summary>
    /// Gets the volume path (e.g. \\.\C:) and drive letter for a partition via Win32_LogicalDiskToPartition.
    /// Returns (null, null) if the partition has no drive letter.
    /// </summary>
    private static (string? VolumePath, string? DriveLetter) GetVolumePathAndLetterForPartition(System.Management.ManagementBaseObject partition)
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
                    return ($"\\\\.\\{deviceId}", deviceId);
                }
            }
        }
        catch
        {
            // Ignore - partition may not have a drive letter
        }

        return (null, null);
    }

    // Well-known GPT type GUIDs
    private static readonly Guid GptBasicData  = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    private static readonly Guid GptEfiSystem  = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    private static readonly Guid GptMsReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    private static readonly Guid GptRecovery   = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    /// <summary>
    /// Classifies a GPT partition type GUID into a human-readable label.
    /// Used for IOCTL-only partitions that WMI skipped.
    /// </summary>
    private static string? ClassifyGptTypeGuid(Guid gptType)
    {
        if (gptType == GptEfiSystem)  return "EFI (ESP)";
        if (gptType == GptMsReserved) return "MSR";
        if (gptType == GptRecovery)   return "Recovery";
        if (gptType == GptBasicData)  return "Primary";
        if (gptType == Guid.Empty)    return null;
        return gptType.ToString();
    }

    /// <summary>
    /// Classifies partition type from WMI Win32_DiskPartition fields.
    /// </summary>
    private static string? ClassifyPartitionType(System.Management.ManagementBaseObject partition)
    {
        try
        {
            var type = partition["Type"]?.ToString() ?? string.Empty;
            // WMI Type strings: "GPT: System", "GPT: Basic Data", "GPT: Unknown", "Installable File System", etc.
            if (type.Contains("System", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("EFI", StringComparison.OrdinalIgnoreCase))
                return "EFI (ESP)";
            if (type.Contains("Basic Data", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("Installable", StringComparison.OrdinalIgnoreCase))
                return "Primary";
            if (type.Contains("Recovery", StringComparison.OrdinalIgnoreCase))
                return "Recovery";
            if (type.Contains("Reserved", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("MSR", StringComparison.OrdinalIgnoreCase))
                return "MSR";

            // Check if it's a recovery partition by checking the bootable flag and small size
            var bootable = partition["Bootable"]?.ToString();
            var sizeStr = partition["Size"]?.ToString() ?? "0";
            if (ulong.TryParse(sizeStr, out var size) && size > 0 && size < 2UL * 1024 * 1024 * 1024)
            {
                // Small non-system, non-basic partition is likely recovery
                if (!string.IsNullOrEmpty(type) && type.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
                    return "Recovery";
            }

            return !string.IsNullOrEmpty(type) ? type : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Enriches partitions with volume metadata (label, filesystem, used/free space)
    /// by querying DriveInfo or WMI Win32_Volume for each volume path.
    /// </summary>
    private static void EnrichPartitionsWithVolumeMetadata(List<PartitionInfo> partitions)
    {
        foreach (var part in partitions)
        {
            if (string.IsNullOrEmpty(part.VolumePath))
                continue;

            try
            {
                // Try DriveInfo first (works for drive-letter volumes)
                if (!string.IsNullOrEmpty(part.DriveLetter))
                {
                    var di = new DriveInfo(part.DriveLetter[..1]);
                    if (di.IsReady)
                    {
                        part.FileSystem = di.DriveFormat;
                        part.VolumeLabel = string.IsNullOrWhiteSpace(di.VolumeLabel) ? null : di.VolumeLabel;
                        part.UsedSpace = (ulong)(di.TotalSize - di.TotalFreeSpace);
                        part.FreeSpace = (ulong)di.TotalFreeSpace;
                        continue;
                    }
                }

                // Fallback: WMI Win32_Volume for GUID-only volumes
                EnrichFromWmiVolume(part);
            }
            catch (Exception ex)
            {
                Log.Debug("Failed to enrich partition {Part}: {Error}", part.PartitionNumber, ex.Message);
            }
        }
    }

    /// <summary>
    /// Queries WMI Win32_Volume for a volume matching the given device path.
    /// Works for \\?\Volume{GUID} paths that don't have drive letters.
    /// </summary>
    private static void EnrichFromWmiVolume(PartitionInfo part)
    {
        try
        {
            // Win32_Volume DeviceID uses \\?\Volume{GUID}\ (with trailing backslash)
            var guidPath = part.VolumePath!;
            if (!guidPath.EndsWith('\\'))
                guidPath += "\\";

            // Normalize \\.\X: to X:\ for matching
            string wmiFilter;
            if (guidPath.StartsWith("\\\\.\\") && guidPath.Length >= 6 && guidPath[5] == ':')
            {
                // Drive letter path — skip WMI for these (DriveInfo should work)
                return;
            }
            else
            {
                // Volume GUID path
                wmiFilter = $"DeviceID='{EscapeWmiString(guidPath)}'";
            }

            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT FileSystem, Label, Capacity, FreeSpace FROM Win32_Volume WHERE {wmiFilter}");
            foreach (var vol in searcher.Get())
            {
                part.FileSystem = vol["FileSystem"]?.ToString();
                var label = vol["Label"]?.ToString();
                part.VolumeLabel = string.IsNullOrWhiteSpace(label) ? null : label;

                if (ulong.TryParse(vol["Capacity"]?.ToString(), out var capacity) &&
                    ulong.TryParse(vol["FreeSpace"]?.ToString(), out var freeSpace))
                {
                    part.FreeSpace = freeSpace;
                    part.UsedSpace = capacity > freeSpace ? capacity - freeSpace : 0;
                }
                break; // take first match
            }
        }
        catch (Exception ex)
        {
            Log.Debug("WMI volume enrichment failed for partition {Part}: {Error}", part.PartitionNumber, ex.Message);
        }
    }

    /// <summary>
    /// Queries MSFT_Disk from the Storage WMI namespace to determine partition style.
    /// Falls back to IOCTL drive layout heuristics if WMI is unavailable.
    /// </summary>
    private static DiskPartitionStyle GetPartitionStyle(uint diskNumber)
    {
        // Try WMI first
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT PartitionStyle FROM MSFT_Disk WHERE Number = {diskNumber}");
            foreach (var result in searcher.Get())
            {
                var style = result["PartitionStyle"]?.ToString();
                return style switch
                {
                    "1" => DiskPartitionStyle.MBR,
                    "2" => DiskPartitionStyle.GPT,
                    _ => DiskPartitionStyle.Unknown,
                };
            }
        }
        catch (Exception ex)
        {
            Log.Debug("WMI partition style query failed for disk {Disk}: {Error}", diskNumber, ex.Message);
        }

        // IOCTL fallback: the drive layout header contains partition style
        try
        {
            var layout = DiskApi.GetDriveLayout(diskNumber);
            // If any partition has a non-empty GPT type GUID, it's GPT
            if (layout.Any(e => e.GptTypeGuid != Guid.Empty))
                return DiskPartitionStyle.GPT;
            if (layout.Count > 0)
                return DiskPartitionStyle.MBR; // Has partitions but no GPT GUIDs → MBR
        }
        catch { }

        return DiskPartitionStyle.Unknown;
    }

    private static string EscapeWmiString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    /// <summary>
    /// IOCTL-only disk enumeration fallback for environments without WMI (e.g. WinPE).
    /// Probes \\.\PhysicalDrive0 through \\.\PhysicalDrive31 and reads geometry via IOCTL.
    /// </summary>
    private static List<DiskInfo> EnumerateDisksViaIoctl()
    {
        var disks = new List<DiskInfo>();
        var indices = DiskApi.ProbePhysicalDiskIndices();
        Log.Information("IOCTL probe found {Count} physical disk(s): [{Indices}]",
            indices.Count, string.Join(", ", indices));

        foreach (var idx in indices)
        {
            var geo = DiskApi.GetDiskGeometry(idx);
            if (geo is null) continue;

            // Try to determine partition style from drive layout
            var layout = DiskApi.GetDriveLayout(idx);
            var style = DiskPartitionStyle.Unknown;
            string model = $"Disk {idx}";

            // Guess media type from MEDIA_TYPE
            bool isRemovable = geo.Value.MediaType == 12; // RemovableMedia

            disks.Add(new DiskInfo
            {
                DiskNumber = idx,
                Manufacturer = "Unknown",
                Model = model,
                SerialNumber = "Unknown",
                Size = (ulong)geo.Value.DiskSize,
                PartitionStyle = style,
            });
        }

        return disks;
    }

    /// <summary>
    /// For partitions that have no drive letter (VolumePath == null), enumerate all
    /// volume GUID paths on the system and match them to the target disk by offset.
    /// This resolves volumes on read-only attached VHDXs that don't get drive letters.
    /// </summary>
    private static void ResolveVolumeGuidPaths(uint diskNumber, List<PartitionInfo> partitions)
    {
        // Only bother if there are partitions without a volume path
        var unresolved = partitions.Where(p => string.IsNullOrEmpty(p.VolumePath)).ToList();
        if (unresolved.Count == 0)
            return;

        Log.Debug("Resolving volume GUID paths for {Count} partitions without drive letters on disk {Disk}",
            unresolved.Count, diskNumber);

        try
        {
            var volumeGuids = VolumeApi.EnumerateVolumeGuids();
            Log.Debug("Found {Count} volume GUIDs on system", volumeGuids.Count);

            foreach (var volumeGuid in volumeGuids)
            {
                // Strip trailing backslash to get device path for CreateFile
                var devicePath = VolumeApi.VolumeGuidToDevicePath(volumeGuid);
                var extent = VolumeApi.GetVolumeDiskExtent(devicePath);
                if (extent is null)
                    continue;

                var diskExtent = extent.Value;

                // Check if this volume belongs to our target disk
                if (diskExtent.DiskNumber != diskNumber)
                    continue;

                // Match by starting offset — most reliable way to map volume to partition
                var matchingPartition = unresolved.FirstOrDefault(p =>
                    (long)p.Offset == diskExtent.StartingOffset);

                if (matchingPartition != null)
                {
                    matchingPartition.VolumePath = devicePath;
                    Log.Information(
                        "Resolved volume GUID path for disk {Disk} partition {Part} (offset {Offset}): {Path}",
                        diskNumber, matchingPartition.PartitionNumber, matchingPartition.Offset, devicePath);

                    // Remove from unresolved list
                    unresolved.Remove(matchingPartition);
                    if (unresolved.Count == 0)
                        break;
                }
            }

            if (unresolved.Count > 0)
            {
                Log.Debug("{Count} partitions on disk {Disk} still have no volume path (may be non-NTFS or unmounted)",
                    unresolved.Count, diskNumber);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to resolve volume GUID paths for disk {Disk}", diskNumber);
        }
    }
}
