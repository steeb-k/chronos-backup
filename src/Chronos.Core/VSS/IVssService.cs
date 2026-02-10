namespace Chronos.Core.VSS;

/// <summary>
/// Interface for Volume Shadow Copy Service operations.
/// </summary>
public interface IVssService
{
    /// <summary>
    /// Creates a shadow copy set of the specified volumes (for consistent point-in-time backup).
    /// </summary>
    /// <param name="volumePaths">Volume paths (e.g. "C:\", "E:\") — use drive letter + backslash format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="progress">Optional progress callback; VSS steps (GatherWriterMetadata, PrepareForBackup, etc.) can take 15–60 seconds.</param>
    /// <returns>Disposable snapshot set; use GetSnapshotPath(originalVolumePath) to get the snapshot device path for reading.</returns>
    Task<IVssSnapshotSet> CreateSnapshotSetAsync(IReadOnlyList<string> volumePaths, CancellationToken cancellationToken = default, IProgress<string>? progress = null);

    /// <summary>
    /// Checks if VSS is available on the system.
    /// </summary>
    /// <returns>True if VSS is available.</returns>
    bool IsVssAvailable();
}

/// <summary>
/// Represents a VSS snapshot set. Dispose to release the snapshots.
/// </summary>
public interface IVssSnapshotSet : IDisposable
{
    /// <summary>
    /// Gets the snapshot device path for the given original volume (e.g. "C:\" -> "\\.\GLOBALROOT\Device\HarddiskVolumeShadowCopy5").
    /// Use this path for CreateFile/FSCTL/reading instead of the live volume to avoid sharing violations.
    /// </summary>
    string? GetSnapshotPath(string originalVolumePath);
}
