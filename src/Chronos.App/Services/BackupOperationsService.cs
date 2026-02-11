using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Chronos.Core.Imaging;
using Chronos.Core.Models;
using Chronos.Core.Progress;

namespace Chronos.App.Services;

/// <summary>
/// Singleton service that runs backup operations and reports progress.
/// Progress is stored in observable properties so any ViewModel can bind and display it.
/// </summary>
public partial class BackupOperationsService : ObservableObject, IBackupOperationsService
{
    private readonly IBackupEngine _backupEngine;
    private readonly IVerificationEngine? _verificationEngine;
    private readonly IOperationHistoryService? _historyService;

    [ObservableProperty]
    private bool _isBackupInProgress;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public BackupOperationsService(
        IBackupEngine backupEngine, 
        IVerificationEngine? verificationEngine = null,
        IOperationHistoryService? historyService = null)
    {
        _backupEngine = backupEngine;
        _verificationEngine = verificationEngine;
        _historyService = historyService;
    }

    public async Task StartBackupAsync(BackupJob job, bool verifyAfterBackup)
    {
        if (IsBackupInProgress)
        {
            StatusMessage = "A backup is already in progress";
            return;
        }

        var startTime = DateTime.UtcNow;
        long bytesProcessed = 0;
        string status = "Success";
        string? errorMessage = null;

        try
        {
            IsBackupInProgress = true;
            ProgressPercentage = 0;
            StatusMessage = "Starting backup...";

            // Prevent system sleep during backup
            PowerManagement.PreventSleep();

            var lastUiUpdate = DateTime.MinValue;
            var progress = new Progress<OperationProgress>(p =>
            {
                var now = DateTime.UtcNow;
                if ((now - lastUiUpdate).TotalMilliseconds < 300 && p.PercentComplete < 100)
                    return;
                lastUiUpdate = now;

                ProgressPercentage = p.PercentComplete;
                bytesProcessed = p.BytesProcessed;
                
                var statusText = $"Processing - {p.PercentComplete:F1}%";

                if (p.TotalBytes > 0)
                {
                    statusText += $" ({FormatBytes((ulong)p.BytesProcessed)} / {FormatBytes((ulong)p.TotalBytes)})";
                }

                if (p.BytesPerSecond > 0)
                {
                    statusText += $" - {FormatBytes((ulong)p.BytesPerSecond)}/s";

                    if (p.TotalBytes > p.BytesProcessed)
                    {
                        long remainingBytes = p.TotalBytes - p.BytesProcessed;
                        double remainingSeconds = (double)remainingBytes / p.BytesPerSecond;
                        statusText += $" - ETA: {FormatTimeSpan(TimeSpan.FromSeconds(remainingSeconds))}";
                    }
                }

                if (!string.IsNullOrEmpty(p.StatusMessage))
                {
                    StatusMessage = $"{p.StatusMessage} - {statusText}";
                }
                else
                {
                    StatusMessage = statusText;
                }
            });

            await _backupEngine.ExecuteAsync(job, new ProgressReporter(progress));

            if (verifyAfterBackup && _verificationEngine is not null)
            {
                StatusMessage = "Verifying image...";
                var verifyProgress = new Progress<OperationProgress>(p =>
                {
                    ProgressPercentage = p.PercentComplete;
                    var statusText = $"Verifying - {p.PercentComplete:F1}%";
                    if (p.TotalBytes > 0)
                        statusText += $" ({FormatBytes((ulong)p.BytesProcessed)} / {FormatBytes((ulong)p.TotalBytes)})";
                    StatusMessage = statusText;
                });
                var verifyReporter = new ProgressReporter(verifyProgress);

                var verified = await _verificationEngine.VerifyImageAsync(job.DestinationPath, verifyReporter);

                if (verified)
                    StatusMessage = "Backup completed and verified successfully!";
                else
                {
                    StatusMessage = "Backup completed, but verification failed. The image may be corrupted.";
                    status = "Failed";
                    errorMessage = "Verification failed";
                }
            }
            else
            {
                StatusMessage = "Backup completed successfully!";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Backup cancelled";
            status = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
            status = "Failed";
            errorMessage = ex.Message;
        }
        finally
        {
            // Allow system sleep again
            PowerManagement.AllowSleep();

            IsBackupInProgress = false;

            // Log to history
            if (_historyService is not null)
            {
                var duration = DateTime.UtcNow - startTime;
                var operationType = job.Type == BackupType.DiskClone || job.Type == BackupType.PartitionClone 
                    ? "Clone" 
                    : "Backup";

                _historyService.LogOperation(new OperationHistoryEntry
                {
                    Timestamp = startTime,
                    OperationType = operationType,
                    SourcePath = job.SourcePath,
                    DestinationPath = job.DestinationPath,
                    Status = status,
                    ErrorMessage = errorMessage,
                    BytesProcessed = bytesProcessed,
                    Duration = duration
                });
            }
        }
    }

    public void CancelBackup()
    {
        _backupEngine.CancelAsync();
    }

    private class ProgressReporter : IProgressReporter
    {
        private readonly IProgress<OperationProgress> _progress;

        public ProgressReporter(IProgress<OperationProgress> progress)
        {
            _progress = progress;
        }

        public void Report(OperationProgress progress)
        {
            _progress.Report(progress);
        }
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
