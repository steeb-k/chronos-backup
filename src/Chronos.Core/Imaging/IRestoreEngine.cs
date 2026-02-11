using Chronos.Core.Models;
using Chronos.Core.Progress;

namespace Chronos.Core.Imaging;

/// <summary>
/// Interface for restore operations.
/// </summary>
public interface IRestoreEngine
{
    /// <summary>
    /// Executes a restore job.
    /// </summary>
    /// <param name="job">The restore job configuration.</param>
    /// <param name="progressReporter">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the restore is finished.</returns>
    Task ExecuteAsync(RestoreJob job, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that the restore can proceed safely.
    /// </summary>
    /// <param name="job">The restore job to validate.</param>
    /// <exception cref="InvalidOperationException">Thrown if validation fails with details about why.</exception>
    /// <returns>A task that completes if validation succeeds.</returns>
    Task ValidateRestoreAsync(RestoreJob job);
}
