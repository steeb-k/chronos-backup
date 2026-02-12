using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Chronos.Core.Disk;
using Chronos.Core.Services;
using Chronos.Core.Streams;
using Chronos.Core.VirtualDisk;
using Chronos.Core.Compression;
using Chronos.Core.Models;
using Chronos.Core.Progress;
using Chronos.Core.VSS;
using Serilog;

namespace Chronos.Core.Imaging;

/// <summary>
/// Implementation of backup engine with sector-by-sector copy and compression support.
/// </summary>
public class BackupEngine : IBackupEngine
{
    private const int CopyBufferSize = 2 * 1024 * 1024; // 2 MB
    private readonly IDiskReader _diskReader;
    private readonly IDiskWriter _diskWriter;
    private readonly IVirtualDiskService _virtualDiskService;
    private readonly ICompressionProvider _compressionProvider;
    private readonly IDiskEnumerator _diskEnumerator;
    private readonly IAllocatedRangesProvider _allocatedRangesProvider;
    private readonly IVssService _vssService;
    private CancellationTokenSource? _cancellationTokenSource;
    private long _lastAllocatedBytesCopied;

    public BackupEngine(
        IDiskReader diskReader,
        IDiskWriter diskWriter,
        IVirtualDiskService virtualDiskService,
        ICompressionProvider compressionProvider,
        IDiskEnumerator diskEnumerator,
        IAllocatedRangesProvider allocatedRangesProvider,
        IVssService vssService)
    {
        _diskReader = diskReader ?? throw new ArgumentNullException(nameof(diskReader));
        _diskWriter = diskWriter ?? throw new ArgumentNullException(nameof(diskWriter));
        _virtualDiskService = virtualDiskService ?? throw new ArgumentNullException(nameof(virtualDiskService));
        _compressionProvider = compressionProvider ?? throw new ArgumentNullException(nameof(compressionProvider));
        _diskEnumerator = diskEnumerator ?? throw new ArgumentNullException(nameof(diskEnumerator));
        _allocatedRangesProvider = allocatedRangesProvider ?? throw new ArgumentNullException(nameof(allocatedRangesProvider));
        _vssService = vssService ?? throw new ArgumentNullException(nameof(vssService));
    }

    public async Task ExecuteAsync(BackupJob job, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        Log.Information("Backup started: Source={Source}, Dest={Dest}, Type={Type}", job.SourcePath, job.DestinationPath, job.Type);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cancellationTokenSource.Token;

        try
        {
            // Check if this is a clone operation (destination is a physical disk/partition)
            bool isClone = job.Type == BackupType.DiskClone || job.Type == BackupType.PartitionClone;

            if (isClone)
            {
                await ExecuteCloneAsync(job, progressReporter, ct).ConfigureAwait(false);
            }
            else
            {
                await ExecuteBackupAsync(job, progressReporter, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            progressReporter?.Report(new OperationProgress { StatusMessage = "Operation cancelled" });
            throw;
        }
        catch (Exception ex)
        {
            int win32 = Marshal.GetLastWin32Error();
            Log.Error(ex, "Operation failed. Win32 error: {Win32} (0x{Win32X}). Message: {Message}", win32, win32.ToString("X"), ex.Message);
            progressReporter?.Report(new OperationProgress { StatusMessage = $"Error: {ex.Message}" });
            throw;
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task ExecuteBackupAsync(BackupJob job, IProgressReporter? progressReporter, CancellationToken ct)
    {
        progressReporter?.Report(new OperationProgress { StatusMessage = "Opening source disk..." });

        (uint diskNumber, uint? partitionNumber) = ParseSourcePath(job.SourcePath);
        Log.Debug("Parsed source: Disk={Disk}, Partition={Partition}", diskNumber, partitionNumber);
        DiskReadHandle? sourceHandle = null;

        try
        {
            sourceHandle = partitionNumber.HasValue
                ? await _diskReader.OpenPartitionAsync(diskNumber, partitionNumber.Value, ct).ConfigureAwait(false)
                : await _diskReader.OpenDiskAsync(diskNumber, ct).ConfigureAwait(false);

            ulong sourceSize = await _diskReader.GetSizeAsync(sourceHandle, ct).ConfigureAwait(false);
            Log.Information("Source opened: Size={Size} bytes", sourceSize);
            if (sourceSize == 0)
                throw new InvalidOperationException("Source has zero size");

            string ext = Path.GetExtension(job.DestinationPath).ToLowerInvariant();
            bool isVhdx = ext is ".vhdx" or ".vhd";
            Log.Debug("Destination format: ext={Ext}, isVhdx={IsVhdx}", ext, isVhdx);

            if (isVhdx)
            {
                await CopyToVhdxAsync(sourceHandle, sourceSize, job, progressReporter, ct, diskNumber, partitionNumber).ConfigureAwait(false);
            }
            else
            {
                await CopyToFileAsync(sourceHandle, sourceSize, job, progressReporter, ct).ConfigureAwait(false);
            }

            // Save sidecar metadata alongside the image
            await SaveSidecarAsync(diskNumber, job.DestinationPath, sourceHandle.SectorSize, _lastAllocatedBytesCopied).ConfigureAwait(false);

            progressReporter?.Report(new OperationProgress
            {
                StatusMessage = "Backup completed successfully",
                PercentComplete = 100,
                BytesProcessed = (long)sourceSize,
                TotalBytes = (long)sourceSize
            });
        }
        finally
        {
            sourceHandle?.Dispose();
        }
    }

    private async Task ExecuteCloneAsync(BackupJob job, IProgressReporter? progressReporter, CancellationToken ct)
    {
        progressReporter?.Report(new OperationProgress { StatusMessage = "Opening source disk..." });

        (uint sourceDisk, uint? sourcePartition) = ParseSourcePath(job.SourcePath);
        (uint destDisk, uint? destPartition) = ParseSourcePath(job.DestinationPath); // Reuse same parser
        Log.Information("Clone operation: Source Disk={SDisk}:{SPart}, Dest Disk={DDisk}:{DPart}", 
            sourceDisk, sourcePartition, destDisk, destPartition);

        // Validate source and destination are different
        if (sourceDisk == destDisk && sourcePartition == destPartition)
        {
            throw new InvalidOperationException("Source and destination cannot be the same disk/partition");
        }

        DiskReadHandle? sourceHandle = null;
        DiskWriteHandle? destHandle = null;

        try
        {
            // Open source
            sourceHandle = sourcePartition.HasValue
                ? await _diskReader.OpenPartitionAsync(sourceDisk, sourcePartition.Value, ct).ConfigureAwait(false)
                : await _diskReader.OpenDiskAsync(sourceDisk, ct).ConfigureAwait(false);

            ulong sourceSize = await _diskReader.GetSizeAsync(sourceHandle, ct).ConfigureAwait(false);
            Log.Information("Source opened: Size={Size} bytes", sourceSize);
            if (sourceSize == 0)
                throw new InvalidOperationException("Source has zero size");

            // Open destination for write
            progressReporter?.Report(new OperationProgress { StatusMessage = "Opening destination disk for write..." });
            
            if (destPartition.HasValue)
            {
                destHandle = await _diskWriter.OpenPartitionForWriteAsync(destDisk, destPartition.Value, ct).ConfigureAwait(false);
            }
            else
            {
                string destPath = $"\\\\.\\PhysicalDrive{destDisk}";
                destHandle = await _diskWriter.OpenDiskForWriteAsync(destPath, ct).ConfigureAwait(false);
            }

            Log.Information("Destination opened for write");

            // Copy sectors directly (no VSS for clone, no allocated ranges optimization)
            await CopySectorsAsync(sourceHandle, destHandle, sourceSize, 0, null, null, progressReporter, ct).ConfigureAwait(false);

            progressReporter?.Report(new OperationProgress
            {
                StatusMessage = "Clone completed successfully",
                PercentComplete = 100,
                BytesProcessed = (long)sourceSize,
                TotalBytes = (long)sourceSize
            });
        }
        finally
        {
            sourceHandle?.Dispose();
            destHandle?.Dispose();
        }
    }

    /// <summary>
    /// True if the volume path (e.g. \\.\E:) is the same drive as the destination path.
    /// When true, we can't open the volume for read — the destination file would cause ERROR_SHARING_VIOLATION.
    /// </summary>
    private static bool IsVolumeSameAsDestination(string? volumePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(volumePath) || string.IsNullOrWhiteSpace(destinationPath))
            return false;
        // volumePath = "\\.\E:", extract "E:"
        if (volumePath.Length < 4) return false;
        var volumeDrive = volumePath.Substring(4); // "\\.\".Length = 4 -> "E:"
        var destRoot = Path.GetPathRoot(destinationPath);
        if (string.IsNullOrEmpty(destRoot)) return false;
        var destDrive = destRoot.TrimEnd('\\', '/');
        return string.Equals(volumeDrive, destDrive, StringComparison.OrdinalIgnoreCase);
    }

    private static (uint diskNumber, uint? partitionNumber) ParseSourcePath(string sourcePath)
    {
        if (sourcePath.StartsWith("\\\\.\\PhysicalDrive", StringComparison.OrdinalIgnoreCase))
        {
            string numStr = sourcePath.Replace("\\\\.\\PhysicalDrive", "", StringComparison.OrdinalIgnoreCase);
            if (uint.TryParse(numStr, out uint diskNumber))
                return (diskNumber, null);
        }
        // Partition format: "diskNumber:partitionNumber"
        if (sourcePath.Contains(':'))
        {
            var parts = sourcePath.Split(':');
            if (parts.Length == 2 && uint.TryParse(parts[0], out uint diskNum) && uint.TryParse(parts[1], out uint partNum))
                return (diskNum, partNum);
        }
        throw new InvalidOperationException($"Unsupported source path: {sourcePath}");
    }

    /// <summary>
    /// Saves a sidecar JSON file alongside the image with disk/partition metadata
    /// so the restore UI can display a disk map without mounting the image.
    /// </summary>
    private async Task SaveSidecarAsync(uint diskNumber, string destinationPath, uint sectorSize, long expectedAllocatedBytes = 0)
    {
        try
        {
            var disk = await _diskEnumerator.GetDiskAsync(diskNumber).ConfigureAwait(false);
            var partitions = await _diskEnumerator.GetPartitionsAsync(diskNumber).ConfigureAwait(false);

            if (disk is null)
            {
                Log.Warning("Cannot save sidecar: disk {Disk} not found", diskNumber);
                return;
            }

            var sidecar = ImageSidecar.FromDisk(disk, partitions, sectorSize);
            if (expectedAllocatedBytes > 0)
                sidecar.ExpectedAllocatedBytes = expectedAllocatedBytes;
            await sidecar.SaveAsync(destinationPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Non-fatal — the backup itself succeeded
            Log.Warning(ex, "Failed to save image sidecar for {Path}", destinationPath);
        }
    }

    private async Task CopyToVhdxAsync(
        DiskReadHandle sourceHandle,
        ulong sourceSize,
        BackupJob job,
        IProgressReporter? progressReporter,
        CancellationToken ct,
        uint diskNumber,
        uint? partitionNumber)
    {
        progressReporter?.Report(new OperationProgress { StatusMessage = "Creating destination VHDX..." });

        var destDir = Path.GetDirectoryName(job.DestinationPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
            Log.Debug("Ensured destination directory exists: {Dir}", destDir);
        }

        if (File.Exists(job.DestinationPath))
            File.Delete(job.DestinationPath);

        ulong vhdxSize = sourceSize;
        if (sourceSize > 0 && sourceSize < long.MaxValue)
            vhdxSize = sourceSize;

        Log.Information("Creating and attaching VHDX: Path={Path}, Size={Size}, SectorSize={SectorSize}", job.DestinationPath, vhdxSize, sourceHandle.SectorSize);
        progressReporter?.Report(new OperationProgress { StatusMessage = "Creating and attaching VHDX..." });
        using (var attached = await _virtualDiskService.CreateAndAttachVhdxForWriteAsync(job.DestinationPath, (long)vhdxSize, sourceHandle.SectorSize, ct).ConfigureAwait(false))
        {
            Log.Information("VHDX attached. PhysicalPath={PhysicalPath}", attached.PhysicalPath);
            DiskWriteHandle? destHandle = null;
            IVssSnapshotSet? snapshotSet = null;
            try
            {
                Log.Debug("Opening destination for write: {Path}", attached.PhysicalPath);
                destHandle = await _diskWriter.OpenDiskForWriteAsync(attached.PhysicalPath, ct).ConfigureAwait(false);
                Log.Information("Starting sector copy: {Size} bytes", sourceSize);

                var allParts = await _diskEnumerator.GetPartitionsAsync(diskNumber).ConfigureAwait(false);
                List<PartitionInfo>? partitions;
                if (partitionNumber.HasValue)
                {
                    var part = allParts.FirstOrDefault(p => p.PartitionNumber == partitionNumber.Value);
                    partitions = part != null ? new List<PartitionInfo> { new() { Offset = 0, Size = part.Size, VolumePath = part.VolumePath } } : null;
                }
                else
                {
                    partitions = allParts.OrderBy(p => p.Offset).ToList();
                }
                if (_vssService.IsVssAvailable() && partitions != null && partitions.Count > 0)
                {
                    var volumesToSnapshot = partitions
                        .Where(p => !string.IsNullOrEmpty(p.VolumePath) && !IsVolumeSameAsDestination(p.VolumePath, job.DestinationPath))
                        .Select(p => p.VolumePath!.Replace(@"\\.\", "") + @"\")
                        .Distinct()
                        .ToList();
                    if (volumesToSnapshot.Count > 0)
                    {
                        var vssProgress = progressReporter != null
                            ? new Progress<string>(msg => progressReporter.Report(new OperationProgress { StatusMessage = msg }))
                            : null;
                        snapshotSet = await _vssService.CreateSnapshotSetAsync(volumesToSnapshot, ct, vssProgress).ConfigureAwait(false);
                    }
                }

                var ranges = await BuildCopyRangesAsync(diskNumber, partitionNumber, sourceSize, job.DestinationPath, snapshotSet, sourceHandle.SectorSize, ct).ConfigureAwait(false);
                if (ranges is not null)
                {
                    Log.Debug("CopyToVhdx: using allocated-ranges optimization ({RangeCount} ranges)", ranges.Count);
                    await CopySectorsWithRangesAsync(sourceHandle, destHandle, ranges, sourceSize, partitions, snapshotSet, progressReporter, ct).ConfigureAwait(false);
                }
                else
                {
                    Log.Debug("CopyToVhdx: using full linear copy (no allocated ranges)");
                    await CopySectorsAsync(sourceHandle, destHandle, sourceSize, job.CompressionLevel, partitions, snapshotSet, progressReporter, ct).ConfigureAwait(false);
                }

                Log.Debug("CopyToVhdx: sector copy complete, reporting Finalizing backup");
                progressReporter?.Report(new OperationProgress { StatusMessage = "Finalizing backup...", PercentComplete = 100 });
            }
            finally
            {
                Log.Debug("CopyToVhdx: finally block - disposing snapshot set");
                snapshotSet?.Dispose();
                Log.Debug("CopyToVhdx: disposed snapshot set, disposing dest handle");
                destHandle?.Dispose();
                Log.Debug("CopyToVhdx: disposed dest handle");
            }
        }
    }

    private async Task CopyToFileAsync(
        DiskReadHandle sourceHandle,
        ulong sourceSize,
        BackupJob job,
        IProgressReporter? progressReporter,
        CancellationToken ct)
    {
        var destDir = Path.GetDirectoryName(job.DestinationPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        if (File.Exists(job.DestinationPath))
            File.Delete(job.DestinationPath);

        bool useCompression = job.CompressionLevel > 0;

        if (useCompression)
        {
            progressReporter?.Report(new OperationProgress { StatusMessage = "Compressing to file..." });
            await using var diskStream = new DiskReadStream(_diskReader, sourceHandle, CopyBufferSize);
            await using var progressStream = new ProgressReportingStream(diskStream, sourceSize, progressReporter);
            await using var fileStream = new FileStream(job.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await _compressionProvider.CompressAsync(progressStream, fileStream, job.CompressionLevel, ct).ConfigureAwait(false);
        }
        else
        {
            progressReporter?.Report(new OperationProgress { StatusMessage = "Copying to file..." });
            await using var diskStream = new DiskReadStream(_diskReader, sourceHandle, CopyBufferSize);
            await using var fileStream = new FileStream(job.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous);
            await CopyStreamWithProgressAsync(diskStream, fileStream, sourceSize, progressReporter, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the list of byte ranges to copy. Uses NTFS allocated ranges when available.
    /// Returns null to fall back to full linear copy.
    /// </summary>
    private async Task<List<(long Offset, long Length)>?> BuildCopyRangesAsync(uint diskNumber, uint? partitionNumber, ulong sourceSize, string destinationPath, IVssSnapshotSet? snapshotSet, uint sectorSize, CancellationToken ct)
    {
        if (partitionNumber.HasValue)
        {
            var parts = await _diskEnumerator.GetPartitionsAsync(diskNumber).ConfigureAwait(false);
            var part = parts.FirstOrDefault(p => p.PartitionNumber == partitionNumber.Value);
            var volumePath = part?.VolumePath;
            if (IsVolumeSameAsDestination(volumePath, destinationPath))
                volumePath = null;
            if (volumePath is null)
                return null; // No volume path — fall back to full copy
            var pathForQuery = snapshotSet != null ? snapshotSet.GetSnapshotPath(volumePath) ?? volumePath : volumePath;
            var ranges = await _allocatedRangesProvider.GetAllocatedRangesAsync(pathForQuery, sourceSize, ct).ConfigureAwait(false);
            if (ranges is null || ranges.Count == 0)
                return null;

            // Sanity check: if allocated ranges total exceeds source partition size,
            // the volume path is probably wrong (WMI returned the wrong drive letter).
            long totalAllocated = ranges.Sum(r => r.Length);
            if (totalAllocated > (long)sourceSize)
            {
                Log.Warning("BuildCopyRanges: allocated ranges total ({Total}) exceeds source size ({Source}) — volume path {Path} is likely wrong, falling back to full copy",
                    totalAllocated, sourceSize, volumePath);
                return null;
            }

            return ranges.Select(r => (r.Offset, r.Length)).ToList();
        }

        // Full disk backup
        var partitions = await _diskEnumerator.GetPartitionsAsync(diskNumber).ConfigureAwait(false);
        if (partitions.Count == 0)
            return null;

        var sorted = partitions.OrderBy(p => p.Offset).ToList();
        var result = new List<(long Offset, long Length)>();

        // Always include partition table: sector 0 (MBR) + sectors 1-33 (GPT header/entries).
        // Without this, the VHD appears "not initialized" with no partition data.
        // Use actual sector size from source disk (ARM64 often has 4K sectors).
        const int gptSectors = 34; // MBR + GPT header + partition array
        long gptSize = gptSectors * sectorSize;
        result.Add((0, Math.Min(gptSize, (long)sourceSize)));
        Log.Debug("BuildCopyRanges: added primary partition table range (0, {Size})", Math.Min(gptSize, (long)sourceSize));

        foreach (var part in sorted)
        {
            if (part.Size == 0) continue;

            var volumePath = part.VolumePath;
            if (IsVolumeSameAsDestination(volumePath, destinationPath))
                volumePath = null;
            var pathForQuery = volumePath != null && snapshotSet != null ? snapshotSet.GetSnapshotPath(volumePath) ?? volumePath : volumePath;
            if (volumePath != null && pathForQuery == volumePath && snapshotSet != null)
                Log.Warning("BuildCopyRanges: no snapshot path for volume {Volume}, using live path (may fail with sharing violation)", volumePath);
            var allocRanges = await _allocatedRangesProvider.GetAllocatedRangesAsync(pathForQuery, part.Size, ct).ConfigureAwait(false);

            if (allocRanges is null)
            {
                // Non-NTFS or query failed - include full partition
                result.Add(((long)part.Offset, (long)part.Size));
            }
            else if (allocRanges.Count == 0)
            {
                // Fully unallocated - skip (don't add any range)
            }
            else
            {
                // Sanity check: if ranges total exceeds partition size, volume path was wrong
                long rangeTotal = allocRanges.Sum(r => r.Length);
                if (rangeTotal > (long)part.Size)
                {
                    Log.Warning("BuildCopyRanges: partition {Num} ranges total ({Total}) exceeds size ({Size}) — wrong VolumePath {Path}, using full partition",
                        part.PartitionNumber, rangeTotal, part.Size, volumePath);
                    result.Add(((long)part.Offset, (long)part.Size));
                }
                else
                {
                    foreach (var r in allocRanges)
                    {
                        result.Add(((long)part.Offset + r.Offset, r.Length));
                    }
                }
            }
        }

        // Include backup GPT at end of disk (required for GPT disks)
        if (sourceSize > (ulong)gptSize)
        {
            long backupStart = (long)sourceSize - gptSize;
            result.Add((backupStart, gptSize));
            Log.Debug("BuildCopyRanges: added backup GPT range ({Start}, {Size})", backupStart, gptSize);
        }

        long totalRangeBytes = result.Sum(r => r.Length);
        Log.Information("BuildCopyRanges: {Count} ranges, {Total} bytes to copy (disk size {DiskSize})", result.Count, totalRangeBytes, sourceSize);
        return result;
    }

    private async Task CopySectorsWithRangesAsync(
        DiskReadHandle sourceHandle,
        DiskWriteHandle destHandle,
        List<(long Offset, long Length)> ranges,
        ulong totalBytes,
        IReadOnlyList<PartitionInfo>? partitions,
        IVssSnapshotSet? snapshotSet,
        IProgressReporter? progressReporter,
        CancellationToken ct)
    {
        byte[] buffer = new byte[CopyBufferSize];
        int sectorSize = (int)sourceHandle.SectorSize;
        long totalToCopy = ranges.Sum(r => r.Length);
        long bytesCopied = 0;
        long bytesSkipped = 0;
        var sw = Stopwatch.StartNew();
        long lastReported = 0;
        var lastProgressTime = DateTime.MinValue;
        var snapshotHandles = new Dictionary<ulong, DiskReadHandle>();
        int rangeIndex = 0;

        Log.Debug("CopySectorsWithRanges: starting, {RangeCount} ranges, {TotalToCopy} bytes", ranges.Count, totalToCopy);

        try
        {
            foreach (var (offset, length) in ranges)
            {
                ct.ThrowIfCancellationRequested();
                rangeIndex++;
                if (rangeIndex <= 2 || rangeIndex == ranges.Count)
                    Log.Debug("CopySectorsWithRanges: range {Index}/{Total} offset={Offset} length={Length} (bytesCopied so far: {Bytes})", rangeIndex, ranges.Count, offset, length, bytesCopied);
                if (rangeIndex >= (int)(ranges.Count * 0.9))
                    await Task.Yield();

                long remaining = length;
                long currentOffset = offset;
                (DiskReadHandle readHandle, long readBaseOffset) = ResolveReadSource(sourceHandle, offset, partitions, snapshotSet, snapshotHandles);

                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(remaining, buffer.Length);
                    int sectorsToRead = (toRead + sectorSize - 1) / sectorSize;
                    long sectorOffset = (currentOffset - readBaseOffset) / sectorSize;

                    int bytesRead = await _diskReader.ReadSectorsAsync(readHandle, buffer, sectorOffset, sectorsToRead, ct).ConfigureAwait(false);
                    int sectorsToWrite = bytesRead / sectorSize;
                    int bytesToProcess = sectorsToWrite * sectorSize;
                    if (bytesToProcess == 0 && toRead > 0)
                    {
                        Log.Warning("Read returned 0 bytes at offset {Offset}, expected {Expected}; skipping remainder of range to avoid infinite loop", currentOffset, toRead);
                        break;
                    }
                    if (bytesToProcess > remaining)
                    {
                        sectorsToWrite = (int)(remaining / sectorSize);
                        bytesToProcess = sectorsToWrite * sectorSize;
                    }

                    if (bytesToProcess > 0)
                    {
                        if (!IsBufferZero(buffer.AsSpan(0, bytesToProcess)))
                        {
                            await _diskWriter.WriteSectorsAsync(destHandle, buffer, currentOffset / sectorSize, sectorsToWrite, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            bytesSkipped += bytesToProcess;
                        }
                    }

                    bytesCopied += bytesToProcess;
                    currentOffset += bytesToProcess;
                    remaining -= bytesToProcess;

                var elapsed = sw.Elapsed.TotalSeconds;
                double percent = totalToCopy > 0 ? (double)bytesCopied / totalToCopy * 100 : 0;
                var now = DateTime.UtcNow;
                bool shouldReport = elapsed > 0.5 &&
                    bytesCopied - lastReported > 10 * 1024 * 1024 &&
                    (now - lastProgressTime).TotalMilliseconds >= 500;
                if (shouldReport)
                {
                    lastReported = bytesCopied;
                    lastProgressTime = now;
                    long bytesPerSecond = (long)(bytesCopied / elapsed);
                    double remainingSeconds = bytesPerSecond > 0 ? (totalToCopy - bytesCopied) / (double)bytesPerSecond : 0;
                    progressReporter?.Report(new OperationProgress
                    {
                        StatusMessage = "Copying allocated sectors...",
                        BytesProcessed = bytesCopied,
                        TotalBytes = (long)totalToCopy,
                        PercentComplete = Math.Min(100, percent),
                        BytesPerSecond = bytesPerSecond,
                        TimeRemaining = TimeSpan.FromSeconds(remainingSeconds)
                    });
                }
                }
            }
        }
        finally
        {
            foreach (var h in snapshotHandles.Values)
                h.Dispose();
            Log.Debug("CopySectorsWithRanges: copy loop completed, bytesCopied={Bytes}, disposed snapshot handles", bytesCopied);
        }

        if (bytesSkipped > 0)
            Log.Information("Zero-block compression: skipped {Bytes} bytes", bytesSkipped);

        // Detect incomplete copies — bytesCopied should match totalToCopy
        if (bytesCopied < totalToCopy)
        {
            Log.Error("Incomplete backup: copied {Copied} of {Expected} bytes ({Percent:F1}%)",
                bytesCopied, totalToCopy, 100.0 * bytesCopied / totalToCopy);
            throw new IOException(
                $"Backup incomplete: only {bytesCopied:N0} of {totalToCopy:N0} bytes were copied " +
                $"({100.0 * bytesCopied / totalToCopy:F1}%). The destination image is unusable. " +
                $"This may indicate a disk I/O error or device disconnection during the backup.");
        }

        Log.Information("NTFS allocated-ranges optimization: copied {Copied} of {Total} bytes ({Percent:F1}% read)", totalToCopy, totalBytes, 100.0 * totalToCopy / totalBytes);
        _lastAllocatedBytesCopied = totalToCopy;

        Log.Debug("CopySectorsWithRanges: reporting 100% progress");
        progressReporter?.Report(new OperationProgress
        {
            StatusMessage = "Copying allocated sectors...",
            BytesProcessed = totalToCopy,
            TotalBytes = (long)totalToCopy,
            PercentComplete = 100
        });
        Log.Debug("CopySectorsWithRanges: finished");
    }

    private (DiskReadHandle handle, long baseOffset) ResolveReadSource(DiskReadHandle diskHandle, long offset, IReadOnlyList<PartitionInfo>? partitions, IVssSnapshotSet? snapshotSet, Dictionary<ulong, DiskReadHandle> cache)
    {
        if (partitions == null || snapshotSet == null)
            return (diskHandle, 0);

        var part = partitions.FirstOrDefault(p => offset >= (long)p.Offset && offset < (long)(p.Offset + p.Size));
        if (part == null || string.IsNullOrEmpty(part.VolumePath))
            return (diskHandle, 0);

        var snapshotPath = snapshotSet.GetSnapshotPath(part.VolumePath);
        if (string.IsNullOrEmpty(snapshotPath))
            return (diskHandle, 0);

        if (!cache.TryGetValue(part.Offset, out var handle))
        {
            handle = _diskReader.OpenPathForReadAsync(snapshotPath, part.Size, CancellationToken.None).GetAwaiter().GetResult();
            cache[part.Offset] = handle;
        }
        return (handle, (long)part.Offset);
    }

    private async Task CopySectorsAsync(
        DiskReadHandle sourceHandle,
        DiskWriteHandle destHandle,
        ulong totalBytes,
        int _,
        IReadOnlyList<PartitionInfo>? partitions,
        IVssSnapshotSet? snapshotSet,
        IProgressReporter? progressReporter,
        CancellationToken ct)
    {
        byte[] buffer = new byte[CopyBufferSize];
        int sectorSize = (int)sourceHandle.SectorSize;
        int sectorsPerRead = buffer.Length / sectorSize;
        long totalSectors = (long)(totalBytes / sourceHandle.SectorSize);
        long sectorsProcessed = 0;
        var sw = Stopwatch.StartNew();
        long lastReported = 0;
        var snapshotHandles = new Dictionary<ulong, DiskReadHandle>();
        long bytesSkipped = 0;

        try
        {
            while (sectorsProcessed < totalSectors)
            {
                ct.ThrowIfCancellationRequested();

                long byteOffset = sectorsProcessed * sectorSize;
                (var readHandle, long readBaseOffset) = ResolveReadSource(sourceHandle, byteOffset, partitions, snapshotSet, snapshotHandles);
                long sectorOffsetInHandle = (byteOffset - readBaseOffset) / sectorSize;

                int sectorsToProcess = (int)Math.Min(sectorsPerRead, totalSectors - sectorsProcessed);
                int bytesRead = await _diskReader.ReadSectorsAsync(readHandle, buffer, sectorOffsetInHandle, sectorsToProcess, ct).ConfigureAwait(false);
                int sectorsWritten = bytesRead / sectorSize;
                if (sectorsWritten > 0)
                {
                    if (!IsBufferZero(buffer.AsSpan(0, bytesRead)))
                    {
                        await _diskWriter.WriteSectorsAsync(destHandle, buffer, sectorsProcessed, sectorsWritten, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        bytesSkipped += bytesRead;
                    }
                }

                sectorsProcessed += sectorsToProcess;
                long bytesProcessed = sectorsProcessed * sectorSize;

                var elapsed = sw.Elapsed.TotalSeconds;
                if (elapsed > 0.5 && bytesProcessed - lastReported > 10 * 1024 * 1024)
                {
                    lastReported = bytesProcessed;
                    long bytesPerSecond = (long)(bytesProcessed / elapsed);
                    double percent = totalBytes > 0 ? (double)bytesProcessed / totalBytes * 100 : 0;
                    double remainingSeconds = bytesPerSecond > 0 ? ((long)totalBytes - bytesProcessed) / (double)bytesPerSecond : 0;

                    progressReporter?.Report(new OperationProgress
                    {
                        StatusMessage = "Copying sectors...",
                        BytesProcessed = bytesProcessed,
                        TotalBytes = (long)totalBytes,
                        PercentComplete = Math.Min(100, percent),
                        BytesPerSecond = bytesPerSecond,
                        TimeRemaining = TimeSpan.FromSeconds(remainingSeconds)
                    });
                }
            }
        }
        finally
        {
            foreach (var h in snapshotHandles.Values)
                h.Dispose();
        }

        if (bytesSkipped > 0)
            Log.Information("Zero-block compression: skipped {Bytes} bytes ({Percent:F1}%)", bytesSkipped, 100.0 * bytesSkipped / totalBytes);

        progressReporter?.Report(new OperationProgress
        {
            StatusMessage = "Copying sectors...",
            BytesProcessed = (long)totalBytes,
            TotalBytes = (long)totalBytes,
            PercentComplete = 100
        });
    }

    private async Task CopyStreamWithProgressAsync(
        Stream source,
        Stream destination,
        ulong totalBytes,
        IProgressReporter? progressReporter,
        CancellationToken ct)
    {
        byte[] buffer = new byte[CopyBufferSize];
        long totalCopied = 0;
        var sw = Stopwatch.StartNew();
        long lastReported = 0;

        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            totalCopied += read;

            var elapsed = sw.Elapsed.TotalSeconds;
            if (elapsed > 0.5 && totalCopied - lastReported > 10 * 1024 * 1024)
            {
                lastReported = totalCopied;
                long bytesPerSecond = (long)(totalCopied / elapsed);
                double percent = totalBytes > 0 ? (double)totalCopied / totalBytes * 100 : 0;
                double remainingSeconds = bytesPerSecond > 0 ? ((long)totalBytes - totalCopied) / (double)bytesPerSecond : 0;

                progressReporter?.Report(new OperationProgress
                {
                    StatusMessage = "Copying...",
                    BytesProcessed = totalCopied,
                    TotalBytes = (long)totalBytes,
                    PercentComplete = Math.Min(100, percent),
                    BytesPerSecond = bytesPerSecond,
                    TimeRemaining = TimeSpan.FromSeconds(remainingSeconds)
                });
            }
        }

        progressReporter?.Report(new OperationProgress
        {
            BytesProcessed = totalCopied,
            TotalBytes = (long)totalBytes,
            PercentComplete = 100
        });
    }

    private static bool IsBufferZero(ReadOnlySpan<byte> span)
    {
        return span.IndexOfAnyExcept((byte)0) < 0;
    }

    public Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();
        return Task.CompletedTask;
    }
}
