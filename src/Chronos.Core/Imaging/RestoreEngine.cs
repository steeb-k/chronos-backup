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

    public RestoreEngine(
        IDiskReader diskReader,
        IDiskWriter diskWriter,
        IVirtualDiskService virtualDiskService,
        IDiskEnumerator diskEnumerator)
    {
        _diskReader = diskReader ?? throw new ArgumentNullException(nameof(diskReader));
        _diskWriter = diskWriter ?? throw new ArgumentNullException(nameof(diskWriter));
        _virtualDiskService = virtualDiskService ?? throw new ArgumentNullException(nameof(virtualDiskService));
        _diskEnumerator = diskEnumerator ?? throw new ArgumentNullException(nameof(diskEnumerator));
    }

    public async Task ExecuteAsync(RestoreJob job, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        Log.Information("Restore started: Source={Source}, Target={Target}", job.SourceImagePath, job.TargetPath);

        try
        {
            // Validate before starting
            if (!await ValidateRestoreAsync(job))
            {
                throw new InvalidOperationException("Restore validation failed. Cannot proceed.");
            }

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

    public async Task<bool> ValidateRestoreAsync(RestoreJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        try
        {
            // Check if source image exists
            if (!File.Exists(job.SourceImagePath))
            {
                Log.Error("Source image does not exist: {Path}", job.SourceImagePath);
                return false;
            }

            // Parse target path
            (uint targetDisk, uint? targetPartition) = ParseTargetPath(job.TargetPath);
            Log.Debug("Validating restore target: Disk={Disk}, Partition={Partition}", targetDisk, targetPartition);

            // Get system disk info to prevent accidental system disk overwrites
            var disks = await _diskEnumerator.GetDisksAsync();
            var targetDiskInfo = disks.FirstOrDefault(d => d.DiskNumber == targetDisk);

            if (targetDiskInfo == null)
            {
                Log.Error("Target disk {Disk} not found", targetDisk);
                return false;
            }

            // Check if target is system disk (has boot/system partition)
            if (targetDiskInfo.IsSystemDisk || targetDiskInfo.IsBootDisk)
            {
                Log.Warning("Target disk {Disk} is a system or boot disk. Restore would be destructive!", targetDisk);
                // Return false unless ForceOverwrite is explicitly set
                if (!job.ForceOverwrite)
                {
                    Log.Error("ForceOverwrite not set. Refusing to restore to system/boot disk.");
                    return false;
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
                    Log.Error("Target partition {Disk}:{Partition} not found", targetDisk, targetPartition.Value);
                    return false;
                }
                targetSize = (long)targetPart.Size;
            }
            else
            {
                targetSize = (long)targetDiskInfo.Size;
            }

            // Verify target is large enough
            if (targetSize < sourceSize)
            {
                Log.Error("Target size ({TargetSize} bytes) is smaller than source size ({SourceSize} bytes)", targetSize, sourceSize);
                return false;
            }

            Log.Information("Restore validation passed: Source={Source} ({SourceSize} bytes), Target={Target} ({TargetSize} bytes)",
                job.SourceImagePath, sourceSize, job.TargetPath, targetSize);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during restore validation");
            return false;
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

            // Copy sectors
            await CopySectorsAsync(sourceHandle, targetHandle, sourceSize, job.VerifyDuringRestore, progressReporter, ct);

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
}
