using Chronos.Core.Models;

namespace Chronos.App.Services;

/// <summary>
/// Service that manages backup operations and their progress. Survives navigation
/// so backups continue running when the user switches views.
/// </summary>
public interface IBackupOperationsService
{
    /// <summary>Whether a backup is currently in progress.</summary>
    bool IsBackupInProgress { get; }

    /// <summary>Progress percentage (0–100).</summary>
    double ProgressPercentage { get; }

    /// <summary>Current status message.</summary>
    string StatusMessage { get; }

    /// <summary>Starts a backup. Runs in background; progress is reported via properties.</summary>
    Task StartBackupAsync(BackupJob job, bool verifyAfterBackup);

    /// <summary>Cancels the current backup.</summary>
    void CancelBackup();
}
