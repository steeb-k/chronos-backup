using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Chronos.Native.Win32;
using Serilog;

namespace Chronos.Core.Services;

// Layout must match Windows SET_DISK_ATTRIBUTES structure
[StructLayout(LayoutKind.Sequential)]
internal struct SetDiskAttributes
{
    public uint Version;         // sizeof(SetDiskAttributes) = 40
    public byte Persist;         // BOOLEAN
    public byte Reserved1a;
    public byte Reserved1b;
    public byte Reserved1c;
    public ulong Attributes;
    public ulong AttributesMask;
    public uint Reserved2a;
    public uint Reserved2b;
    public uint Reserved2c;
    public uint Reserved2d;
}

/// <summary>
/// Service for preparing disks for restore operations (dismounting volumes, etc.).
/// </summary>
public interface IDiskPreparationService
{
    /// <summary>
    /// Prepares a disk for restore by forcefully dismounting all volumes on it.
    /// This will lock each volume, dismount it, and hold the lock so writes can proceed.
    /// </summary>
    /// <param name="diskNumber">The disk number (e.g., 0 for PhysicalDrive0).</param>
    /// <param name="takeOffline">If true, the disk is taken offline to prevent Windows from re-mounting volumes.
    /// Set to false for partition-level restores where the partition device path must remain accessible.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A disposable that holds the volume locks. Dispose when done writing.</returns>
    Task<IDisposable> PrepareDiskForRestoreAsync(uint diskNumber, bool takeOffline = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dismounts and locks a single partition's volume on a disk.
    /// Use this for partition-level restores to avoid dismounting other volumes
    /// (e.g., the one hosting the source VHDX file).
    /// </summary>
    Task<IDisposable> PreparePartitionForRestoreAsync(uint diskNumber, uint partitionNumber, CancellationToken cancellationToken = default);
}

public class DiskPreparationService : IDiskPreparationService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DiskPreparationService>();
    private readonly IDiskEnumerator _diskEnumerator;

    // Constants for CreateFile access
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    public DiskPreparationService(IDiskEnumerator diskEnumerator)
    {
        _diskEnumerator = diskEnumerator ?? throw new ArgumentNullException(nameof(diskEnumerator));
    }

    public async Task<IDisposable> PreparePartitionForRestoreAsync(uint diskNumber, uint partitionNumber, CancellationToken cancellationToken = default)
    {
        var lockedVolumes = new LockedVolumeSet();

        await _diskEnumerator.RefreshAsync();
        var partitions = await _diskEnumerator.GetPartitionsAsync(diskNumber);
        var partition = partitions.FirstOrDefault(p => p.PartitionNumber == partitionNumber);

        if (partition is null)
        {
            Log.Warning("PreparePartition: partition {Part} not found on disk {Disk}", partitionNumber, diskNumber);
            return lockedVolumes;
        }

        if (string.IsNullOrEmpty(partition.VolumePath))
        {
            Log.Debug("PreparePartition: partition {Disk}:{Part} has no volume path, nothing to dismount",
                diskNumber, partitionNumber);
            return lockedVolumes;
        }

        Log.Information("Dismounting volume {VolumePath} (Disk {Disk}, Partition {Part})",
            partition.VolumePath, diskNumber, partitionNumber);

        try
        {
            var volumeHandle = DiskApi.CreateFile(
                partition.VolumePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

            if (volumeHandle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                Log.Warning("Could not open volume {Volume} for dismount, error {Error}", partition.VolumePath, err);
                return lockedVolumes;
            }

            // Lock with retry
            bool locked = false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (DiskApi.DeviceIoControl(volumeHandle, DiskApi.FSCTL_LOCK_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    locked = true;
                    Log.Debug("Locked volume {Volume} on attempt {Attempt}", partition.VolumePath, attempt + 1);
                    break;
                }
                int lockErr = Marshal.GetLastWin32Error();
                Log.Warning("Lock attempt {Attempt}/5 failed for {Volume}, error {Error}. Retrying...",
                    attempt + 1, partition.VolumePath, lockErr);
                Thread.Sleep(500);
            }

            if (!locked)
                Log.Warning("Could not lock volume {Volume} after 5 attempts. Attempting dismount anyway.", partition.VolumePath);

            if (!DiskApi.DeviceIoControl(volumeHandle, DiskApi.FSCTL_DISMOUNT_VOLUME,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                int dismountErr = Marshal.GetLastWin32Error();
                Log.Error("Failed to dismount volume {Volume}, error {Error}", partition.VolumePath, dismountErr);
                volumeHandle.Dispose();
                return lockedVolumes;
            }

            Log.Information("Successfully dismounted volume {VolumePath}", partition.VolumePath);
            lockedVolumes.Add(volumeHandle, partition.VolumePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error dismounting volume {VolumePath}", partition.VolumePath);
        }

        return lockedVolumes;
    }

    public async Task<IDisposable> PrepareDiskForRestoreAsync(uint diskNumber, bool takeOffline = true, CancellationToken cancellationToken = default)
    {
        var lockedVolumes = new LockedVolumeSet();

        try
        {
            // Force re-enumerate partitions to get fresh data
            await _diskEnumerator.RefreshAsync();
            var partitions = await _diskEnumerator.GetPartitionsAsync(diskNumber);

            Log.Information("Preparing disk {DiskNumber} for restore: found {Count} partitions", diskNumber, partitions.Count);

            foreach (var partition in partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(partition.VolumePath))
                {
                    Log.Debug("Partition {Disk}:{Part} has no volume path (no drive letter), skipping dismount",
                        diskNumber, partition.PartitionNumber);
                    continue;
                }

                Log.Information("Dismounting volume {VolumePath} (Disk {Disk}, Partition {Part})",
                    partition.VolumePath, diskNumber, partition.PartitionNumber);

                try
                {
                    // Open the volume with read+write access for lock/dismount
                    var volumeHandle = DiskApi.CreateFile(
                        partition.VolumePath,
                        GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        FILE_ATTRIBUTE_NORMAL,
                        IntPtr.Zero);

                    if (volumeHandle.IsInvalid)
                    {
                        int err = Marshal.GetLastWin32Error();
                        Log.Warning("Could not open volume {Volume} for dismount, error {Error}. Trying read-only...",
                            partition.VolumePath, err);

                        // Try read-only as fallback
                        volumeHandle = DiskApi.CreateFile(
                            partition.VolumePath,
                            GENERIC_READ,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            IntPtr.Zero,
                            OPEN_EXISTING,
                            FILE_ATTRIBUTE_NORMAL,
                            IntPtr.Zero);

                        if (volumeHandle.IsInvalid)
                        {
                            err = Marshal.GetLastWin32Error();
                            Log.Error("Could not open volume {Volume} at all, error {Error}", partition.VolumePath, err);
                            continue;
                        }
                    }

                    // Step 1: Lock the volume (retry up to 5 times)
                    bool locked = false;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        if (DiskApi.DeviceIoControl(volumeHandle, DiskApi.FSCTL_LOCK_VOLUME,
                            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                        {
                            locked = true;
                            Log.Debug("Locked volume {Volume} on attempt {Attempt}", partition.VolumePath, attempt + 1);
                            break;
                        }

                        int lockErr = Marshal.GetLastWin32Error();
                        Log.Warning("Lock attempt {Attempt}/5 failed for {Volume}, error {Error}. Retrying...",
                            attempt + 1, partition.VolumePath, lockErr);
                        Thread.Sleep(500);
                    }

                    if (!locked)
                    {
                        Log.Warning("Could not lock volume {Volume} after 5 attempts. Attempting dismount anyway.",
                            partition.VolumePath);
                    }

                    // Step 2: Dismount the volume (invalidates all open file handles on the volume)
                    if (!DiskApi.DeviceIoControl(volumeHandle, DiskApi.FSCTL_DISMOUNT_VOLUME,
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    {
                        int dismountErr = Marshal.GetLastWin32Error();
                        Log.Error("Failed to dismount volume {Volume}, error {Error}", partition.VolumePath, dismountErr);
                        volumeHandle.Dispose();
                        continue;
                    }

                    Log.Information("Successfully dismounted volume {VolumePath}", partition.VolumePath);

                    // Keep the handle open with the lock held - this prevents Windows from
                    // re-mounting the volume while we're writing to the disk
                    lockedVolumes.Add(volumeHandle, partition.VolumePath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error dismounting volume {VolumePath}", partition.VolumePath);
                }
            }

            Log.Information("Disk {DiskNumber} preparation complete: {Count} volumes dismounted and locked",
                diskNumber, lockedVolumes.Count);

            // Take the disk offline so Windows completely stops interacting with it
            // This prevents the "needs to be formatted" dialog and Error 19 (write-protect)
            // Skip for partition-level restores, since offline removes partition device paths
            if (!takeOffline)
            {
                Log.Information("Skipping disk offline (partition-level restore mode)");
                return lockedVolumes;
            }

            string physicalDiskPath = $"\\\\.\\PhysicalDrive{diskNumber}";
            var diskHandle = DiskApi.CreateFile(
                physicalDiskPath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (!diskHandle.IsInvalid)
            {
                // Set disk offline
                var attrs = new SetDiskAttributes
                {
                    Version = 40, // sizeof(SET_DISK_ATTRIBUTES)
                    Persist = 0,  // Don't persist across reboot
                    Attributes = DiskApi.DISK_ATTRIBUTE_OFFLINE,
                    AttributesMask = DiskApi.DISK_ATTRIBUTE_OFFLINE
                };

                int size = Marshal.SizeOf<SetDiskAttributes>();
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(attrs, ptr, false);
                    bool offlineOk = DiskApi.DeviceIoControl(
                        diskHandle,
                        DiskApi.IOCTL_DISK_SET_DISK_ATTRIBUTES,
                        ptr, (uint)size,
                        IntPtr.Zero, 0,
                        out _,
                        IntPtr.Zero);

                    if (offlineOk)
                    {
                        Log.Information("Disk {DiskNumber} taken offline successfully", diskNumber);
                        lockedVolumes.SetDiskHandle(diskHandle, diskNumber);
                    }
                    else
                    {
                        int err = Marshal.GetLastWin32Error();
                        Log.Warning("Failed to take disk {DiskNumber} offline: Win32={Error} (0x{ErrorX}). Proceeding with volume locks only.",
                            diskNumber, err, err.ToString("X"));
                        diskHandle.Dispose();
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                Log.Warning("Could not open physical disk {Path} for offline: Win32={Error}", physicalDiskPath, err);
            }

            return lockedVolumes;
        }
        catch
        {
            lockedVolumes.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Holds locked volume handles open until disposed, preventing Windows from re-mounting.
    /// </summary>
    private class LockedVolumeSet : IDisposable
    {
        private readonly List<(SafeFileHandle Handle, string Path)> _handles = new();
        private SafeFileHandle? _diskHandle;
        private uint _diskNumber;
        private bool _disposed;

        public int Count => _handles.Count;

        public void Add(SafeFileHandle handle, string path)
        {
            _handles.Add((handle, path));
        }

        public void SetDiskHandle(SafeFileHandle handle, uint diskNumber)
        {
            _diskHandle = handle;
            _diskNumber = diskNumber;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Bring the disk back online FIRST
            if (_diskHandle is not null && !_diskHandle.IsInvalid)
            {
                try
                {
                    // Clear both offline and read-only attributes.
                    // Windows sometimes marks a disk read-only after a full-disk
                    // restore (the partition table was replaced while offline).
                    var attrs = new SetDiskAttributes
                    {
                        Version = 40,
                        Persist = 0,
                        Attributes = 0, // Clear both flags
                        AttributesMask = DiskApi.DISK_ATTRIBUTE_OFFLINE | DiskApi.DISK_ATTRIBUTE_READ_ONLY
                    };

                    int size = Marshal.SizeOf<SetDiskAttributes>();
                    IntPtr ptr = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(attrs, ptr, false);
                        bool onlineOk = DiskApi.DeviceIoControl(
                            _diskHandle,
                            DiskApi.IOCTL_DISK_SET_DISK_ATTRIBUTES,
                            ptr, (uint)size,
                            IntPtr.Zero, 0,
                            out _,
                            IntPtr.Zero);

                        if (onlineOk)
                            Log.Information("Disk {DiskNumber} brought back online", _diskNumber);
                        else
                            Log.Warning("Failed to bring disk {DiskNumber} online: Win32={Error}",
                                _diskNumber, Marshal.GetLastWin32Error());
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }

                    // Force Windows to re-read the partition table
                    DiskApi.DeviceIoControl(_diskHandle, DiskApi.IOCTL_DISK_UPDATE_PROPERTIES,
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    Log.Debug("Sent IOCTL_DISK_UPDATE_PROPERTIES to disk {DiskNumber}", _diskNumber);

                    _diskHandle.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error bringing disk {DiskNumber} back online", _diskNumber);
                    _diskHandle.Dispose();
                }
            }

            // Then unlock volumes
            foreach (var (handle, path) in _handles)
            {
                try
                {
                    DiskApi.DeviceIoControl(handle, DiskApi.FSCTL_UNLOCK_VOLUME,
                        IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    handle.Dispose();
                    Log.Debug("Released lock on volume {Path}", path);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error releasing volume lock on {Path}", path);
                }
            }
            _handles.Clear();
        }
    }
}
