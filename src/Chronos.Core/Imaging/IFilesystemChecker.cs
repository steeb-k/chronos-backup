using Chronos.Core.Models;
using Chronos.Core.Progress;

namespace Chronos.Core.Imaging;

/// <summary>
/// Performs a filesystem consistency check on a VHDX image after a restore operation.
/// </summary>
public interface IFilesystemChecker
{
    /// <summary>
    /// Mounts the specified VHDX read-only, runs chkdsk /scan (or a boot-sector
    /// fallback when chkdsk is unavailable), and returns the result.
    /// Always dismounts the VHDX before returning, even on failure.
    /// </summary>
    /// <param name="vhdxPath">Full path to the VHDX file to check.</param>
    /// <param name="progressReporter">Optional reporter for status updates during the check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FilesystemCheckResult> CheckAsync(
        string vhdxPath,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);
}
