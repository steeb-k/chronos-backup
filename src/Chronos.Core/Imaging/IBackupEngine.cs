using Chronos.Core.Models;
using Chronos.Core.Progress;

namespace Chronos.Core.Imaging;

/// <summary>
/// Interface for backup operations.
/// </summary>
public interface IBackupEngine
{
    /// <summary>
    /// Executes a backup job.
    /// </summary>
    /// <param name="job">The backup job configuration.</param>
    /// <param name="progressReporter">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the backup is finished.</returns>
    Task ExecuteAsync(BackupJob job, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the current backup operation.
    /// </summary>
    Task CancelAsync();
}
