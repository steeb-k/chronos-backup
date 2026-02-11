namespace Chronos.Core.VirtualDisk;

/// <summary>
/// Represents an attached VHDX that can be written to. Dispose to detach.
/// </summary>
public interface IAttachedVhdx : IDisposable
{
    /// <summary>
    /// Gets the physical disk path (e.g. \\.\PhysicalDrive2) for sector writes.
    /// </summary>
    string PhysicalPath { get; }
}

/// <summary>
/// Interface for virtual disk operations (VHD/VHDX).
/// </summary>
public interface IVirtualDiskService
{
    /// <summary>
    /// Creates a new dynamic VHDX file.
    /// </summary>
    /// <param name="path">The path where the VHDX will be created.</param>
    /// <param name="maxSizeBytes">The maximum size in bytes.</param>
    /// <param name="sectorSizeInBytes">The logical sector size (512 or 4096). Defaults to 512.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the VHDX is created.</returns>
    Task CreateDynamicVhdxAsync(string path, long maxSizeBytes, uint sectorSizeInBytes = 512, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an existing VHDX file.
    /// </summary>
    /// <param name="path">The path to the VHDX file.</param>
    /// <param name="readOnly">Whether to open in read-only mode.</param>
    /// <returns>A handle to the virtual disk.</returns>
    Task<IntPtr> OpenVhdxAsync(string path, bool readOnly = true);

    /// <summary>
    /// Closes a virtual disk handle.
    /// </summary>
    /// <param name="handle">The handle to close.</param>
    void CloseVhdx(IntPtr handle);

    /// <summary>
    /// Mounts a VHDX to a drive letter.
    /// </summary>
    /// <param name="path">The path to the VHDX file.</param>
    /// <param name="readOnly">Whether to mount read-only.</param>
    /// <returns>The assigned drive letter.</returns>
    Task<char> MountToDriveLetterAsync(string path, bool readOnly = true);

    /// <summary>
    /// Mounts a VHDX to a folder.
    /// </summary>
    /// <param name="path">The path to the VHDX file.</param>
    /// <param name="mountPoint">The folder path where to mount.</param>
    /// <param name="readOnly">Whether to mount read-only.</param>
    /// <returns>A task that completes when mounting is done.</returns>
    Task MountToFolderAsync(string path, string mountPoint, bool readOnly = true);

    /// <summary>
    /// Dismounts a mounted VHDX.
    /// </summary>
    /// <param name="path">The path to the VHDX file.</param>
    /// <returns>A task that completes when dismounting is done.</returns>
    Task DismountAsync(string path);

    /// <summary>
    /// Attaches an existing VHDX for read/write access and returns the physical disk path for sector writes.
    /// Call Dispose on the returned object to detach when done.
    /// </summary>
    /// <param name="path">The path to the VHDX file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An attached VHDX handle. Dispose to detach.</returns>
    Task<IAttachedVhdx> AttachVhdxForWriteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new VHDX and attaches it for write in one operation (avoids close/reopen that causes Error 87).
    /// Call Dispose on the returned object to detach when done.
    /// </summary>
    /// <param name="path">The path where the VHDX will be created.</param>
    /// <param name="maxSizeBytes">The maximum size in bytes.</param>
    /// <param name="sectorSizeInBytes">The logical sector size (512 or 4096). Must match source disk for GPT compatibility.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An attached VHDX handle. Dispose to detach.</returns>
    Task<IAttachedVhdx> CreateAndAttachVhdxForWriteAsync(string path, long maxSizeBytes, uint sectorSizeInBytes = 512, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches an existing VHDX for read-only access and returns the physical disk path for sector reads.
    /// Call Dispose on the returned object to detach when done.
    /// </summary>
    /// <param name="path">The path to the VHDX file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An attached VHDX handle. Dispose to detach.</returns>
    Task<IAttachedVhdx> AttachVhdxReadOnlyAsync(string path, CancellationToken cancellationToken = default);
}
