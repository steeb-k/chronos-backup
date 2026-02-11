using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Chronos.Core.Disk;
using Chronos.Core.Services;
using Chronos.Core.VirtualDisk;
using Chronos.Core.Models;
using Chronos.Core.Progress;
using Serilog;

namespace Chronos.Core.Imaging;

/// <summary>
/// Implementation of restore engine for restoring disk images to physical disks/partitions.
/// </summary>
public class RestoreEngine : IRestoreEngine
{
    private const int CopyBufferSize = 2 * 1024 * 1024; // 2 MB
    private readonly IDiskReader _diskReader;
    private readonly IDiskWriter _diskWriter;
    private readonly IVirtualDiskService _virtualDiskService;
    private readonly IDiskEnumerator _diskEnumerator;
    private readonly IDiskPreparationService _diskPreparation;
    private readonly IAllocatedRangesProvider _allocatedRangesProvider;

    public RestoreEngine(
        IDiskReader diskReader,
        IDiskWriter diskWriter,
        IVirtualDiskService virtualDiskService,
        IDiskEnumerator diskEnumerator,
        IDiskPreparationService diskPreparation,
        IAllocatedRangesProvider allocatedRangesProvider)
    {
        _diskReader = diskReader ?? throw new ArgumentNullException(nameof(diskReader));
        _diskWriter = diskWriter ?? throw new ArgumentNullException(nameof(diskWriter));
        _virtualDiskService = virtualDiskService ?? throw new ArgumentNullException(nameof(virtualDiskService));
        _diskEnumerator = diskEnumerator ?? throw new ArgumentNullException(nameof(diskEnumerator));
        _diskPreparation = diskPreparation ?? throw new ArgumentNullException(nameof(diskPreparation));
        _allocatedRangesProvider = allocatedRangesProvider ?? throw new ArgumentNullException(nameof(allocatedRangesProvider));
    }

    public async Task ExecuteAsync(RestoreJob job, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        Log.Information("Restore started: Source={Source}, Target={Target}", job.SourceImagePath, job.TargetPath);

        try
        {
            // Validate before starting
            await ValidateRestoreAsync(job);

            // Parse target to get disk number
            (uint targetDisk, uint? targetPartition) = ParseTargetPath(job.TargetPath);

            // Prepare disk for restore (dismount and lock all volumes)
            progressReporter?.Report(new OperationProgress { StatusMessage = "Preparing target disk (dismounting volumes)..." });
            Log.Information("Preparing disk {DiskNumber} for restore: dismounting volumes", targetDisk);
            IDisposable? volumeLocks = null;
            try
            {
                volumeLocks = await _diskPreparation.PrepareDiskForRestoreAsync(targetDisk, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to automatically prepare disk {DiskNumber}. Will attempt restore anyway.", targetDisk);
                progressReporter?.Report(new OperationProgress { StatusMessage = "Warning: Could not dismount all volumes. Proceeding..." });
            }

            try
            {
                progressReporter?.Report(new OperationProgress { StatusMessage = "Opening source image..." });

                // Determine if source is VHDX or raw image
                string ext = Path.GetExtension(job.SourceImagePath).ToLowerInvariant();
                bool isVhdx = ext is ".vhdx" or ".vhd";

                if (isVhdx)
                {
                    await RestoreFromVhdxAsync(job, progressReporter, cancellationToken);
                }
                else
                {
                    await RestoreFromRawImageAsync(job, progressReporter, cancellationToken);
                }

                progressReporter?.Report(new OperationProgress
                {
                    StatusMessage = "Restore completed successfully",
                    PercentComplete = 100
                });

                Log.Information("Restore completed successfully");
            }
            finally
            {
                // Release volume locks so Windows can re-mount
                if (volumeLocks != null)
                {
                    Log.Debug("Releasing volume locks after restore");
                    volumeLocks.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            progressReporter?.Report(new OperationProgress { StatusMessage = "Restore cancelled" });
            Log.Warning("Restore cancelled by user");
            throw;
        }
        catch (Exception ex)
        {
            int win32 = Marshal.GetLastWin32Error();
            Log.Error(ex, "Restore failed. Win32 error: {Win32} (0x{Win32X}). Message: {Message}", win32, win32.ToString("X"), ex.Message);
            progressReporter?.Report(new OperationProgress { StatusMessage = $"Error: {ex.Message}" });
            throw;
        }
    }

    public async Task ValidateRestoreAsync(RestoreJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        try
        {
            // Check if source image exists
            if (!File.Exists(job.SourceImagePath))
            {
                string errorMsg = $"Source image does not exist: {job.SourceImagePath}";
                Log.Error(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            // Parse target path
            (uint targetDisk, uint? targetPartition) = ParseTargetPath(job.TargetPath);
            Log.Debug("Validating restore target: Disk={Disk}, Partition={Partition}", targetDisk, targetPartition);

            // Get system disk info to prevent accidental system disk overwrites
            var disks = await _diskEnumerator.GetDisksAsync();
            var targetDiskInfo = disks.FirstOrDefault(d => d.DiskNumber == targetDisk);

            if (targetDiskInfo == null)
            {
                string errorMsg = $"Target disk {targetDisk} not found";
                Log.Error(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            // Check if target is system disk (has boot/system partition)
            if (targetDiskInfo.IsSystemDisk || targetDiskInfo.IsBootDisk)
            {
                Log.Warning("Target disk {Disk} is a system or boot disk. Restore would be destructive!", targetDisk);
                // Throw unless ForceOverwrite is explicitly set
                if (!job.ForceOverwrite)
                {
                    string errorMsg = "Target is a system or boot disk. For safety, restore is blocked unless ForceOverwrite is explicitly enabled.";
                    Log.Error(errorMsg);
                    throw new InvalidOperationException(errorMsg);
                }
                Log.Warning("ForceOverwrite is set. Proceeding with system/boot disk restore.");
            }

            // Get source image size
            long sourceSize = 0;
            string ext = Path.GetExtension(job.SourceImagePath).ToLowerInvariant();
            bool isVhdx = ext is ".vhdx" or ".vhd";

            if (isVhdx)
            {
                // For VHDX, we need to get the virtual size
                // We'll do this by attaching it read-only temporarily
                using var attached = await _virtualDiskService.AttachVhdxReadOnlyAsync(job.SourceImagePath, CancellationToken.None);
                uint sourceDiskNumber = ParsePhysicalDrivePath(attached.PhysicalPath);
                var sourceHandle = await _diskReader.OpenDiskAsync(sourceDiskNumber, CancellationToken.None);
                try
                {
                    sourceSize = (long)await _diskReader.GetSizeAsync(sourceHandle, CancellationToken.None);
                }
                finally
                {
                    sourceHandle?.Dispose();
                }
            }
            else
            {
                // Raw image file
                var fileInfo = new FileInfo(job.SourceImagePath);
                sourceSize = fileInfo.Length;
            }

            // Get target size
            long targetSize = 0;
            if (targetPartition.HasValue)
            {
                var partitions = await _diskEnumerator.GetPartitionsAsync(targetDisk);
                var targetPart = partitions.FirstOrDefault(p => p.PartitionNumber == targetPartition.Value);
                if (targetPart == null)
                {
                    string errorMsg = $"Target partition {targetDisk}:{targetPartition.Value} not found";
                    Log.Error(errorMsg);
                    throw new InvalidOperationException(errorMsg);
                }
                targetSize = (long)targetPart.Size;
            }
            else
            {
                targetSize = (long)targetDiskInfo.Size;
            }

            // Verify target is large enough for allocated data
            // For smart restore (VHDX), we can restore to smaller targets if allocated data fits
            // We allow the restore and will check at runtime if ranges exceed target size
            if (targetSize < sourceSize)
            {
                long sizeDifference = sourceSize - targetSize;
                double percentDifference = (double)sizeDifference / sourceSize;
                
                if (isVhdx)
                {
                    // Smart restore mode - we can potentially restore to a smaller drive
                    // Warn but allow - the actual restore will fail gracefully if allocated data doesn't fit
                    Log.Warning("Target ({TargetSize}) is smaller than source image ({SourceSize}). " +
                        "Smart restore will attempt to copy only allocated data. If allocated data exceeds target size, restore will fail.",
                        FormatBytes((ulong)targetSize), FormatBytes((ulong)sourceSize));
                }
                else
                {
                    // Raw image - must fit exactly (with small tolerance for geometry)
                    const long toleranceBytes = 10 * 1024 * 1024; // 10MB
                    double tolerancePercent = 0.005; // 0.5%
                    
                    if (sizeDifference > toleranceBytes && percentDifference > tolerancePercent)
                    {
                        string errorMsg = $"Target size ({targetSize:N0} bytes / {FormatBytes((ulong)targetSize)}) is too small for raw image ({sourceSize:N0} bytes / {FormatBytes((ulong)sourceSize)}). Difference: {FormatBytes((ulong)sizeDifference)} ({percentDifference:P2})";
                        Log.Error(errorMsg);
                        throw new InvalidOperationException(errorMsg);
                    }
                    Log.Warning("Target is slightly smaller than source ({SizeDiff} bytes, {PercentDiff:P2}), but within tolerance. Proceeding.", sizeDifference, percentDifference);
                }
            }

            // Validate sector size compatibility
            var sidecar = await ImageSidecar.LoadAsync(job.SourceImagePath).ConfigureAwait(false);
            if (sidecar?.SourceSectorSize is uint sourceSectorSize && sourceSectorSize > 0)
            {
                // Query target disk sector size
                uint targetSectorSize = 512; // default
                try
                {
                    using var targetHandle = await _diskReader.OpenDiskAsync(targetDisk, CancellationToken.None).ConfigureAwait(false);
                    targetSectorSize = targetHandle.SectorSize;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not query target disk sector size, assuming 512 bytes");
                }

                if (sourceSectorSize != targetSectorSize)
                {
                    string errorMsg = $"Sector size mismatch: source image has {sourceSectorSize}-byte sectors, " +
                        $"target disk has {targetSectorSize}-byte sectors. " +
                        $"Cross-sector-size restore is not supported because GPT partition tables use sector-relative addresses. " +
                        $"Restoring would corrupt the partition layout.";
                    Log.Error(errorMsg);
                    throw new InvalidOperationException(errorMsg);
                }
                Log.Debug("Sector size validation passed: source={Source}, target={Target}", sourceSectorSize, targetSectorSize);
            }
            else
            {
                Log.Debug("No source sector size in sidecar metadata (legacy image), skipping sector size validation");
            }

            Log.Information("Restore validation passed: Source={Source} ({SourceSize} bytes), Target={Target} ({TargetSize} bytes)",
                job.SourceImagePath, sourceSize, job.TargetPath, targetSize);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Log.Error(ex, "Error during restore validation");
            throw new InvalidOperationException($"Restore validation failed: {ex.Message}", ex);
        }
    }

    private async Task RestoreFromVhdxAsync(RestoreJob job, IProgressReporter? progressReporter, CancellationToken ct)
    {
        progressReporter?.Report(new OperationProgress { StatusMessage = "Attaching source VHDX..." });

        // Attach VHDX read-only
        using var attached = await _virtualDiskService.AttachVhdxReadOnlyAsync(job.SourceImagePath, ct);
        Log.Information("Source VHDX attached: PhysicalPath={PhysicalPath}", attached.PhysicalPath);

        DiskReadHandle? sourceHandle = null;
        DiskWriteHandle? targetHandle = null;

        try
        {
            // Open source for read - extract disk number from PhysicalPath (e.g. "\\.\PhysicalDrive2")
            progressReporter?.Report(new OperationProgress { StatusMessage = "Opening source disk..." });
            uint sourceDiskNumber = ParsePhysicalDrivePath(attached.PhysicalPath);
            sourceHandle = await _diskReader.OpenDiskAsync(sourceDiskNumber, ct);
            ulong sourceSize = await _diskReader.GetSizeAsync(sourceHandle, ct);
            Log.Information("Source opened: Size={Size} bytes", sourceSize);

            // Open target for write
            progressReporter?.Report(new OperationProgress { StatusMessage = "Opening target disk..." });
            (uint targetDisk, uint? targetPartition) = ParseTargetPath(job.TargetPath);

            if (targetPartition.HasValue)
            {
                targetHandle = await _diskWriter.OpenPartitionForWriteAsync(targetDisk, targetPartition.Value, ct);
            }
            else
            {
                string targetPath = $"\\\\.\\PhysicalDrive{targetDisk}";
                targetHandle = await _diskWriter.OpenDiskForWriteAsync(targetPath, ct);
            }

            Log.Information("Target opened for write: {Path}", job.TargetPath);

            // Get target size for smart restore validation
            var targetDisks = await _diskEnumerator.GetDisksAsync();
            var targetDiskInfo = targetDisks.FirstOrDefault(d => d.DiskNumber == targetDisk);
            ulong targetSize = targetDiskInfo?.Size ?? 0;

            // Try to build allocated ranges for sparse restore
            progressReporter?.Report(new OperationProgress { StatusMessage = "Analyzing disk structure for smart restore..." });
            var ranges = await BuildRestoreRangesAsync(sourceDiskNumber, sourceSize, targetSize, ct);

            if (ranges is not null && ranges.Count > 0)
            {
                long totalToCopy = ranges.Sum(r => r.Length);
                Log.Information("Smart restore: {RangeCount} ranges, {ToCopy} bytes to copy (source disk {SourceSize} bytes, target {TargetSize} bytes)",
                    ranges.Count, totalToCopy, sourceSize, targetSize);
                await CopySectorsWithRangesAsync(sourceHandle, targetHandle, ranges, sourceSize, progressReporter, ct);
            }
            else
            {
                Log.Information("Full restore: no allocated ranges available, copying entire disk");
                await CopySectorsAsync(sourceHandle, targetHandle, sourceSize, job.VerifyDuringRestore, progressReporter, ct);
            }

            progressReporter?.Report(new OperationProgress { StatusMessage = "Flushing buffers...", PercentComplete = 99 });
        }
        finally
        {
            sourceHandle?.Dispose();
            targetHandle?.Dispose();
        }
    }

    private async Task RestoreFromRawImageAsync(RestoreJob job, IProgressReporter? progressReporter, CancellationToken ct)
    {
        progressReporter?.Report(new OperationProgress { StatusMessage = "Opening raw image file..." });

        DiskWriteHandle? targetHandle = null;

        try
        {
            // Get source file size
            var fileInfo = new FileInfo(job.SourceImagePath);
            ulong sourceSize = (ulong)fileInfo.Length;
            Log.Information("Raw image file: Size={Size} bytes", sourceSize);

            // Open target for write
            progressReporter?.Report(new OperationProgress { StatusMessage = "Opening target disk..." });
            (uint targetDisk, uint? targetPartition) = ParseTargetPath(job.TargetPath);

            if (targetPartition.HasValue)
            {
                targetHandle = await _diskWriter.OpenPartitionForWriteAsync(targetDisk, targetPartition.Value, ct);
            }
            else
            {
                string targetPath = $"\\\\.\\PhysicalDrive{targetDisk}";
                targetHandle = await _diskWriter.OpenDiskForWriteAsync(targetPath, ct);
            }

            Log.Information("Target opened for write: {Path}", job.TargetPath);

            // Copy from file to disk
            await CopyFromFileToDisAsync(job.SourceImagePath, targetHandle, sourceSize, progressReporter, ct);

            progressReporter?.Report(new OperationProgress { StatusMessage = "Flushing buffers...", PercentComplete = 99 });
        }
        finally
        {
            targetHandle?.Dispose();
        }
    }

    private async Task CopySectorsAsync(
        DiskReadHandle sourceHandle,
        DiskWriteHandle targetHandle,
        ulong totalBytes,
        bool verifyDuringRestore,
        IProgressReporter? progressReporter,
        CancellationToken ct)
    {
        byte[] buffer = new byte[CopyBufferSize];
        // Note: We can't verify during restore since we'd need to read from the write handle
        // Verification would require closing and reopening the handle, which is complex
        // Instead, we'll just log a warning if verification was requested

        if (verifyDuringRestore)
        {
            Log.Warning("Verification during restore is not supported - verification will be skipped");
        }

        int sectorSize = (int)sourceHandle.SectorSize;
        int sectorsPerRead = buffer.Length / sectorSize;
        long totalSectors = (long)(totalBytes / sourceHandle.SectorSize);
        long sectorsProcessed = 0;

        var sw = Stopwatch.StartNew();
        long lastReported = 0;

        Log.Debug("Starting sector copy: {TotalSectors} sectors, sector size {SectorSize}", totalSectors, sectorSize);

        while (sectorsProcessed < totalSectors)
        {
            ct.ThrowIfCancellationRequested();

            int sectorsToProcess = (int)Math.Min(sectorsPerRead, totalSectors - sectorsProcessed);

            // Read from source
            int bytesRead = await _diskReader.ReadSectorsAsync(sourceHandle, buffer, sectorsProcessed, sectorsToProcess, ct);
            int sectorsRead = bytesRead / sectorSize;

            if (sectorsRead > 0)
            {
                // Write to target
                await _diskWriter.WriteSectorsAsync(targetHandle, buffer, sectorsProcessed, sectorsRead, ct);
            }

            sectorsProcessed += sectorsToProcess;
            long bytesProcessed = sectorsProcessed * sectorSize;

            // Report progress
            var elapsed = sw.Elapsed.TotalSeconds;
            if (elapsed > 0.5 && bytesProcessed - lastReported > 10 * 1024 * 1024)
            {
                lastReported = bytesProcessed;
                long bytesPerSecond = (long)(bytesProcessed / elapsed);
                double percent = totalBytes > 0 ? (double)bytesProcessed / totalBytes * 100 : 0;
                double remainingSeconds = bytesPerSecond > 0 ? ((long)totalBytes - bytesProcessed) / (double)bytesPerSecond : 0;

                progressReporter?.Report(new OperationProgress
                {
                    StatusMessage = "Restoring...",
                    BytesProcessed = bytesProcessed,
                    TotalBytes = (long)totalBytes,
                    PercentComplete = Math.Min(99, percent),
                    BytesPerSecond = bytesPerSecond,
                    TimeRemaining = TimeSpan.FromSeconds(remainingSeconds)
                });
            }
        }

        progressReporter?.Report(new OperationProgress
        {
            StatusMessage = "Restoring...",
            BytesProcessed = (long)totalBytes,
            TotalBytes = (long)totalBytes,
            PercentComplete = 99
        });

        Log.Information("Sector copy completed: {Bytes} bytes", totalBytes);
    }

    private async Task CopyFromFileToDisAsync(
        string sourceFilePath,
        DiskWriteHandle targetHandle,
        ulong totalBytes,
        IProgressReporter? progressReporter,
        CancellationToken ct)
    {
        byte[] buffer = new byte[CopyBufferSize];
        long totalCopied = 0;
        var sw = Stopwatch.StartNew();
        long lastReported = 0;

        int sectorSize = (int)targetHandle.SectorSize;

        await using var fileStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous);

        long sectorOffset = 0;

        int read;
        while ((read = await fileStream.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            // Write sectors (must be sector-aligned)
            int sectorsToWrite = read / sectorSize;
            if (sectorsToWrite > 0)
            {
                await _diskWriter.WriteSectorsAsync(targetHandle, buffer, sectorOffset, sectorsToWrite, ct);
                sectorOffset += sectorsToWrite;
            }

            totalCopied += read;

            // Report progress
            var elapsed = sw.Elapsed.TotalSeconds;
            if (elapsed > 0.5 && totalCopied - lastReported > 10 * 1024 * 1024)
            {
                lastReported = totalCopied;
                long bytesPerSecond = (long)(totalCopied / elapsed);
                double percent = totalBytes > 0 ? (double)totalCopied / totalBytes * 100 : 0;
                double remainingSeconds = bytesPerSecond > 0 ? ((long)totalBytes - totalCopied) / (double)bytesPerSecond : 0;

                progressReporter?.Report(new OperationProgress
                {
                    StatusMessage = "Restoring from raw image...",
                    BytesProcessed = totalCopied,
                    TotalBytes = (long)totalBytes,
                    PercentComplete = Math.Min(99, percent),
                    BytesPerSecond = bytesPerSecond,
                    TimeRemaining = TimeSpan.FromSeconds(remainingSeconds)
                });
            }
        }

        progressReporter?.Report(new OperationProgress
        {
            BytesProcessed = totalCopied,
            TotalBytes = (long)totalBytes,
            PercentComplete = 99
        });

        Log.Information("Raw image copy completed: {Bytes} bytes", totalCopied);
    }

    private static (uint diskNumber, uint? partitionNumber) ParseTargetPath(string targetPath)
    {
        // Format: "PhysicalDriveN" or "N" for disk, "N:P" for partition
        targetPath = targetPath.Replace("\\\\.\\PhysicalDrive", "", StringComparison.OrdinalIgnoreCase);
        targetPath = targetPath.Replace("PhysicalDrive", "", StringComparison.OrdinalIgnoreCase);

        if (targetPath.Contains(':'))
        {
            var parts = targetPath.Split(':');
            if (parts.Length == 2 && uint.TryParse(parts[0], out uint diskNum) && uint.TryParse(parts[1], out uint partNum))
                return (diskNum, partNum);
        }
        else if (uint.TryParse(targetPath, out uint diskNumber))
        {
            return (diskNumber, null);
        }

        throw new InvalidOperationException($"Unsupported target path: {targetPath}");
    }

    private static uint ParsePhysicalDrivePath(string physicalPath)
    {
        // Extract disk number from path like "\\.\PhysicalDrive2"
        string path = physicalPath.Replace("\\\\.\\PhysicalDrive", "", StringComparison.OrdinalIgnoreCase);
        path = path.Replace("PhysicalDrive", "", StringComparison.OrdinalIgnoreCase);

        if (uint.TryParse(path, out uint diskNumber))
        {
            return diskNumber;
        }

        throw new InvalidOperationException($"Unable to parse physical drive path: {physicalPath}");
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Builds the list of byte ranges to copy from the source VHDX.
    /// Includes MBR/GPT headers + allocated ranges from each partition with a volume.
    /// Ranges are capped to targetSize to handle slight size differences.
    /// </summary>
    private async Task<List<(long Offset, long Length)>?> BuildRestoreRangesAsync(uint sourceDiskNumber, ulong sourceSize, ulong targetSize, CancellationToken ct)
    {
        try
        {
            // Force disk enumeration refresh to pick up the attached VHDX
            await _diskEnumerator.RefreshAsync();
            var partitions = await _diskEnumerator.GetPartitionsAsync(sourceDiskNumber);

            if (partitions is null || partitions.Count == 0)
            {
                Log.Debug("BuildRestoreRanges: no partitions found on disk {Disk}", sourceDiskNumber);
                return null;
            }

            Log.Debug("BuildRestoreRanges: found {Count} partitions on source disk {Disk}", partitions.Count, sourceDiskNumber);

            var result = new List<(long Offset, long Length)>();

            // Always include the first 1MB (MBR/GPT header + protective MBR + primary GPT)
            const long gptHeaderSize = 1024 * 1024;
            result.Add((0, gptHeaderSize));
            Log.Debug("BuildRestoreRanges: added GPT header range (0, {Size})", gptHeaderSize);

            // For each partition with a volume path, get allocated ranges
            foreach (var part in partitions)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(part.VolumePath))
                {
                    // No volume path - include entire partition (could be recovery, EFI, etc.)
                    if (part.Offset > 0 && part.Size > 0)
                    {
                        result.Add(((long)part.Offset, (long)part.Size));
                        Log.Debug("BuildRestoreRanges: partition {PartNum} has no volume, including entire partition ({Offset}, {Size})",
                            part.PartitionNumber, part.Offset, part.Size);
                    }
                    continue;
                }

                Log.Debug("BuildRestoreRanges: querying allocated ranges for partition {PartNum} at {VolumePath}",
                    part.PartitionNumber, part.VolumePath);

                var allocRanges = await _allocatedRangesProvider.GetAllocatedRangesAsync(part.VolumePath, part.Size, ct);

                if (allocRanges is null || allocRanges.Count == 0)
                {
                    // Couldn't get allocated ranges - include entire partition
                    result.Add(((long)part.Offset, (long)part.Size));
                    Log.Debug("BuildRestoreRanges: no allocated ranges for partition {PartNum}, including entire partition", part.PartitionNumber);
                }
                else
                {
                    // Add each allocated range, offset by partition start
                    foreach (var r in allocRanges)
                    {
                        result.Add(((long)part.Offset + r.Offset, r.Length));
                    }
                    Log.Debug("BuildRestoreRanges: partition {PartNum} has {Count} allocated ranges", part.PartitionNumber, allocRanges.Count);
                }
            }

            // Note: We intentionally skip the backup GPT at the end of source disk.
            // The backup GPT position depends on disk size, so if target is smaller,
            // the source backup GPT won't be at the right position anyway.
            // Windows will regenerate the backup GPT on first mount.
            Log.Debug("BuildRestoreRanges: skipping backup GPT (Windows will regenerate it)");

            // Sort and merge overlapping ranges
            result = MergeOverlappingRanges(result);

            // Cap any ranges that extend past target size (handles slight size differences)
            for (int i = 0; i < result.Count; i++)
            {
                var (offset, length) = result[i];
                long rangeEnd = offset + length;
                if ((ulong)rangeEnd > targetSize)
                {
                    if ((ulong)offset >= targetSize)
                    {
                        // Entire range is past target - remove it
                        Log.Warning("BuildRestoreRanges: removing range ({Offset}, {Length}) - starts past target size {TargetSize}",
                            offset, length, targetSize);
                        result.RemoveAt(i);
                        i--;
                    }
                    else
                    {
                        // Truncate range to fit
                        long newLength = (long)targetSize - offset;
                        Log.Warning("BuildRestoreRanges: truncating range ({Offset}, {Length}) to ({Offset}, {NewLength}) to fit target size {TargetSize}",
                            offset, length, offset, newLength, targetSize);
                        result[i] = (offset, newLength);
                    }
                }
            }

            long totalRangeBytes = result.Sum(r => r.Length);
            Log.Information("BuildRestoreRanges: {Count} ranges, {Total} bytes to copy (source disk {DiskSize} bytes, {Percent:F1}%)",
                result.Count, totalRangeBytes, sourceSize, 100.0 * totalRangeBytes / (double)sourceSize);

            return result;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BuildRestoreRanges failed, will fall back to full copy");
            return null;
        }
    }

    private static List<(long Offset, long Length)> MergeOverlappingRanges(List<(long Offset, long Length)> ranges)
    {
        if (ranges.Count <= 1) return ranges;

        var sorted = ranges.OrderBy(r => r.Offset).ToList();
        var merged = new List<(long Offset, long Length)>();

        var current = sorted[0];
        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            long currentEnd = current.Offset + current.Length;

            if (next.Offset <= currentEnd)
            {
                // Overlapping or adjacent - merge
                long newEnd = Math.Max(currentEnd, next.Offset + next.Length);
                current = (current.Offset, newEnd - current.Offset);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);

        return merged;
    }

    private async Task CopySectorsWithRangesAsync(
        DiskReadHandle sourceHandle,
        DiskWriteHandle targetHandle,
        List<(long Offset, long Length)> ranges,
        ulong totalDiskSize,
        IProgressReporter? progressReporter,
        CancellationToken ct)
    {
        byte[] buffer = new byte[CopyBufferSize];
        int sectorSize = (int)sourceHandle.SectorSize;
        long totalToCopy = ranges.Sum(r => r.Length);
        long bytesCopied = 0;
        long bytesSkippedZero = 0;
        var sw = Stopwatch.StartNew();
        long lastReported = 0;
        int rangeIndex = 0;

        Log.Debug("CopySectorsWithRanges: starting, {RangeCount} ranges, {TotalToCopy} bytes", ranges.Count, totalToCopy);

        foreach (var (offset, length) in ranges)
        {
            ct.ThrowIfCancellationRequested();
            rangeIndex++;
            if (rangeIndex <= 3 || rangeIndex == ranges.Count)
                Log.Debug("CopySectorsWithRanges: range {Index}/{Total} offset={Offset} length={Length}", rangeIndex, ranges.Count, offset, length);

            long remaining = length;
            long currentOffset = offset;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(remaining, buffer.Length);
                int sectorsToRead = (toRead + sectorSize - 1) / sectorSize;
                long sectorOffset = currentOffset / sectorSize;

                int bytesRead = await _diskReader.ReadSectorsAsync(sourceHandle, buffer, sectorOffset, sectorsToRead, ct);
                int sectorsRead = bytesRead / sectorSize;
                int bytesToProcess = sectorsRead * sectorSize;

                if (bytesToProcess == 0 && toRead > 0)
                {
                    Log.Warning("Read returned 0 bytes at offset {Offset}, expected {Expected}; skipping remainder of range", currentOffset, toRead);
                    break;
                }

                if (bytesToProcess > remaining)
                {
                    sectorsRead = (int)(remaining / sectorSize);
                    bytesToProcess = sectorsRead * sectorSize;
                }

                if (bytesToProcess > 0)
                {
                    // Skip zero buffers (additional optimization on top of allocated ranges)
                    if (!IsBufferZero(buffer.AsSpan(0, bytesToProcess)))
                    {
                        await _diskWriter.WriteSectorsAsync(targetHandle, buffer, currentOffset / sectorSize, sectorsRead, ct);
                    }
                    else
                    {
                        bytesSkippedZero += bytesToProcess;
                    }
                }

                bytesCopied += bytesToProcess;
                currentOffset += bytesToProcess;
                remaining -= bytesToProcess;

                // Report progress
                var elapsed = sw.Elapsed.TotalSeconds;
                if (elapsed > 0.5 && bytesCopied - lastReported > 10 * 1024 * 1024)
                {
                    lastReported = bytesCopied;
                    double percent = totalToCopy > 0 ? (double)bytesCopied / totalToCopy * 100 : 0;
                    long bytesPerSecond = (long)(bytesCopied / elapsed);
                    double remainingSeconds = bytesPerSecond > 0 ? (totalToCopy - bytesCopied) / (double)bytesPerSecond : 0;

                    progressReporter?.Report(new OperationProgress
                    {
                        StatusMessage = "Restoring (smart mode)...",
                        BytesProcessed = bytesCopied,
                        TotalBytes = totalToCopy,
                        PercentComplete = Math.Min(99, percent),
                        BytesPerSecond = bytesPerSecond,
                        TimeRemaining = TimeSpan.FromSeconds(remainingSeconds)
                    });
                }
            }
        }

        if (bytesSkippedZero > 0)
            Log.Information("Zero-block optimization: skipped {Bytes} bytes of zeros within allocated ranges", bytesSkippedZero);

        Log.Information("Smart restore: copied {Copied} bytes ({Percent:F1}% of disk), skipped {Skipped} bytes unallocated + {SkippedZero} bytes zeros",
            bytesCopied, 100.0 * bytesCopied / (double)totalDiskSize, (long)totalDiskSize - totalToCopy, bytesSkippedZero);

        progressReporter?.Report(new OperationProgress
        {
            StatusMessage = "Restoring (smart mode)...",
            BytesProcessed = totalToCopy,
            TotalBytes = totalToCopy,
            PercentComplete = 99
        });
    }

    private static bool IsBufferZero(ReadOnlySpan<byte> span)
    {
        return span.IndexOfAnyExcept((byte)0) < 0;
    }
}
