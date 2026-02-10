using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Chronos.Native.Win32;
using Serilog;

namespace Chronos.Core.Disk;

/// <summary>
/// Represents a contiguous range of allocated sectors on an NTFS volume.
/// </summary>
public readonly record struct AllocatedRange(long Offset, long Length);

/// <summary>
/// Queries NTFS volumes for allocated cluster ranges using FSCTL_GET_VOLUME_BITMAP.
/// Enables skipping unallocated (zero) regions when backing up.
/// </summary>
public interface IAllocatedRangesProvider
{
    /// <summary>
    /// Gets the allocated ranges for an NTFS volume. Returns null if the query fails (non-NTFS or error).
    /// </summary>
    /// <param name="volumePath">Volume path (e.g. \\.\C:) — required. Use drive letter path, not raw partition path.</param>
    /// <param name="partitionSize">Size of the partition/volume in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of allocated ranges, or null if not available.</returns>
    Task<IReadOnlyList<AllocatedRange>?> GetAllocatedRangesAsync(string? volumePath, ulong partitionSize, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation using FSCTL_GET_VOLUME_BITMAP for volume-level allocated cluster info.
/// (FSCTL_QUERY_ALLOCATED_RANGES is file-level only and fails with ERROR_INVALID_PARAMETER on volume handles.)
/// </summary>
public class AllocatedRangesProvider : IAllocatedRangesProvider
{
    private const int BitmapChunkBytes = 256 * 1024; // 256 KB per DeviceIoControl call
    private const int ERROR_MORE_DATA = 234;

    public async Task<IReadOnlyList<AllocatedRange>?> GetAllocatedRangesAsync(string? volumePath, ulong partitionSize, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(volumePath))
            return null;

        return await Task.Run<IReadOnlyList<AllocatedRange>?>(() =>
        {
            SafeFileHandle? handle = null;
            try
            {
                Log.Debug("Querying allocated ranges (volume bitmap): path={Path}, size={Size}", volumePath, partitionSize);
                handle = VolumeApi.OpenVolumeForRead(volumePath);
                if (handle.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Debug("Failed to open volume for allocated ranges query: Win32={Win32}", err);
                    return null;
                }

                uint clusterSize;
                bool isDevicePath = volumePath.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase) ||
                                   volumePath.Contains("VolumeShadowCopy", StringComparison.OrdinalIgnoreCase);

                if (isDevicePath)
                {
                    // GetDiskFreeSpace does not accept device paths (e.g. VSS snapshot \\.\GLOBALROOT\Device\...).
                    // Use FSCTL_GET_NTFS_VOLUME_DATA which works on the open volume handle.
                    clusterSize = GetClusterSizeFromHandle(handle);
                }
                else
                {
                    string rootPath = volumePath.Length >= 4 ? volumePath.Substring(4) + "\\" : volumePath + "\\";
                    if (!VolumeApi.GetDiskFreeSpace(rootPath, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
                    {
                        int err = Marshal.GetLastWin32Error();
                        Log.Debug("GetDiskFreeSpace failed: Win32={Win32}, trying FSCTL_GET_NTFS_VOLUME_DATA", err);
                        clusterSize = GetClusterSizeFromHandle(handle);
                    }
                    else
                    {
                        clusterSize = sectorsPerCluster * bytesPerSector;
                    }
                }
                if (clusterSize == 0) clusterSize = 4096;

                var ranges = new List<AllocatedRange>();
                long nextLcn = 0;
                int outputSize = Marshal.SizeOf<VolumeApi.VolumeBitmapBufferHeader>() + BitmapChunkBytes;

                IntPtr inputPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VolumeApi.StartingLcnInputBuffer>());
                IntPtr outputPtr = Marshal.AllocHGlobal(outputSize);
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var input = new VolumeApi.StartingLcnInputBuffer { StartingLcn = nextLcn };
                        Marshal.StructureToPtr(input, inputPtr, false);

                        bool ok = VolumeApi.DeviceIoControl(
                            handle,
                            VolumeApi.FSCTL_GET_VOLUME_BITMAP,
                            inputPtr,
                            (uint)Marshal.SizeOf<VolumeApi.StartingLcnInputBuffer>(),
                            outputPtr,
                            (uint)outputSize,
                            out uint bytesReturned,
                            IntPtr.Zero);

                        int err = Marshal.GetLastWin32Error();
                        if (!ok && err != ERROR_MORE_DATA)
                        {
                            Log.Debug("FSCTL_GET_VOLUME_BITMAP failed: Win32={Win32} (0x{Win32X})", err, err.ToString("X"));
                            return null;
                        }

                        var header = Marshal.PtrToStructure<VolumeApi.VolumeBitmapBufferHeader>(outputPtr);
                        int headerSize = Marshal.SizeOf<VolumeApi.VolumeBitmapBufferHeader>();
                        int bitmapBytes = (int)Math.Min((long)bytesReturned - headerSize,
                            (header.BitmapSize + 7) / 8);
                        if (bitmapBytes <= 0)
                            break;

                        IntPtr bitmapPtr = IntPtr.Add(outputPtr, Marshal.SizeOf<VolumeApi.VolumeBitmapBufferHeader>());
                        ParseBitmapToRanges(bitmapPtr, bitmapBytes, header.StartingLcn, (long)clusterSize, ranges);

                        // Advance by clusters actually in this chunk (BitmapSize is total from Start, not received)
                        long clustersInChunk = bitmapBytes * 8L;
                        nextLcn = header.StartingLcn + clustersInChunk;
                        if (ok || err != ERROR_MORE_DATA || clustersInChunk == 0)
                            break;
                    }

                    if (ranges.Count == 0)
                    {
                        Log.Information("Volume has no allocated clusters (fully empty)");
                        return Array.Empty<AllocatedRange>();
                    }

                    long totalAllocated = 0;
                    foreach (var r in ranges)
                        totalAllocated += r.Length;
                    Log.Information("Allocated ranges: {Count} ranges, {Allocated} bytes of {Total} ({Percent:F1}%)",
                        ranges.Count, totalAllocated, partitionSize, 100.0 * totalAllocated / partitionSize);

                    return ranges;
                }
                finally
                {
                    Marshal.FreeHGlobal(inputPtr);
                    Marshal.FreeHGlobal(outputPtr);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Allocated ranges query threw");
                return null;
            }
            finally
            {
                handle?.Dispose();
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Gets cluster size via FSCTL_GET_NTFS_VOLUME_DATA. Works on device paths (VSS snapshots) where GetDiskFreeSpace fails.
    /// </summary>
    private static uint GetClusterSizeFromHandle(SafeFileHandle handle)
    {
        int size = Marshal.SizeOf<VolumeApi.NtfsVolumeDataBuffer>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = VolumeApi.DeviceIoControl(handle, VolumeApi.FSCTL_GET_NTFS_VOLUME_DATA,
                IntPtr.Zero, 0, buf, (uint)size, out uint bytesReturned, IntPtr.Zero);
            if (ok && bytesReturned >= (uint)size)
            {
                var data = Marshal.PtrToStructure<VolumeApi.NtfsVolumeDataBuffer>(buf);
                if (data.BytesPerCluster > 0)
                    return data.BytesPerCluster;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return 4096; // NTFS default
    }

    private static void ParseBitmapToRanges(IntPtr bitmapPtr, int bitmapBytes, long baseLcn, long clusterSize, List<AllocatedRange> ranges)
    {
        for (int i = 0; i < bitmapBytes; i++)
        {
            byte b = Marshal.ReadByte(bitmapPtr, i);
            if (b == 0) continue;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << bit)) != 0)
                {
                    long lcn = baseLcn + (i * 8L + bit);
                    long offset = lcn * clusterSize;
                    // Coalesce contiguous clusters
                    if (ranges.Count > 0 && ranges[^1].Offset + ranges[^1].Length == offset)
                        ranges[^1] = new AllocatedRange(ranges[^1].Offset, ranges[^1].Length + clusterSize);
                    else
                        ranges.Add(new AllocatedRange(offset, clusterSize));
                }
            }
        }
    }
}
