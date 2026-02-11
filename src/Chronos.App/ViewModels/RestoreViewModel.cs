using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.Core.Imaging;
using Chronos.Core.Models;
using Chronos.Core.Services;
using Chronos.Core.Progress;
using System.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.UI.Dispatching;
using Serilog;

namespace Chronos.App.ViewModels;

public partial class RestoreViewModel : ObservableObject
{
    private readonly IRestoreEngine? _restoreEngine;
    private readonly IDiskEnumerator? _diskEnumerator;
    private CancellationTokenSource? _cancellationTokenSource;
    private DispatcherQueue? _dispatcherQueue;

    [ObservableProperty] public partial string SourceImagePath { get; set; } = string.Empty;
    [ObservableProperty] public partial List<DiskInfo> AvailableDisks { get; set; } = new();
    [ObservableProperty] public partial List<PartitionInfo> AvailableTargetPartitions { get; set; } = new();
    [ObservableProperty] public partial DiskInfo? SelectedTargetDisk { get; set; }
    [ObservableProperty] public partial PartitionInfo? SelectedTargetPartition { get; set; }
    [ObservableProperty] public partial bool VerifyDuringRestore { get; set; } = true;
    [ObservableProperty] public partial bool IsRestoreInProgress { get; set; }
    [ObservableProperty] public partial double ProgressPercentage { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarTitle))]
    public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasTargetPartitions { get; set; }

    /// <summary>Sentinel value representing "Entire Disk" in the target partition dropdown.</summary>
    public static readonly PartitionInfo EntireDiskSentinel = new()
    {
        DiskNumber = uint.MaxValue,
        PartitionNumber = uint.MaxValue,
        Size = 0,
        PartitionType = "Entire Disk"
    };

    /// <summary>True when a specific partition is selected (not Entire Disk).</summary>
    public bool IsPartitionRestore => SelectedTargetPartition is not null && SelectedTargetPartition != EntireDiskSentinel;

    // Source image sidecar data for disk map display
    [ObservableProperty] public partial DiskInfo? SourceDisk { get; set; }
    [ObservableProperty] public partial List<PartitionInfo>? SourcePartitions { get; set; }

    /// <summary>True when there is a non-empty status message to show in the InfoBar.</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>True when the current status message represents an error.</summary>
    public bool IsStatusError => StatusMessage.StartsWith("Restore failed", StringComparison.OrdinalIgnoreCase)
                              || StatusMessage.StartsWith("Restore I/O error", StringComparison.OrdinalIgnoreCase)
                              || StatusMessage.StartsWith("Validation failed", StringComparison.OrdinalIgnoreCase)
                              || StatusMessage.StartsWith("Validation error", StringComparison.OrdinalIgnoreCase)
                              || StatusMessage.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);

    /// <summary>InfoBar title based on whether status is an error or informational.</summary>
    public string StatusInfoBarTitle => IsStatusError ? "Error" : 
        (StatusMessage.Contains("completed successfully", StringComparison.OrdinalIgnoreCase) ? "Success" : "Status");
    [ObservableProperty] public partial long BytesProcessed { get; set; }
    [ObservableProperty] public partial long TotalBytes { get; set; }
    [ObservableProperty] public partial long BytesPerSecond { get; set; }
    [ObservableProperty] public partial string EstimatedTimeRemaining { get; set; } = string.Empty;

    public bool CanStartRestore => !string.IsNullOrWhiteSpace(SourceImagePath) && 
        SelectedTargetDisk is not null && 
        !IsRestoreInProgress;

    public RestoreViewModel(IRestoreEngine? restoreEngine = null, IDiskEnumerator? diskEnumerator = null)
    {
        _restoreEngine = restoreEngine;
        _diskEnumerator = diskEnumerator;
        
        // Capture the UI dispatcher queue for marshaling status updates
        try
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            // If we're not on a UI thread, we'll set it later when a command is invoked
            Log.Warning("RestoreViewModel created on non-UI thread, dispatcher will be captured later");
        }
    }

    private void SetStatusMessageSafe(string message)
    {
        // Try to get dispatcher if we don't have it yet
        _dispatcherQueue ??= DispatcherQueue.GetForCurrentThread();

        if (_dispatcherQueue is not null)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                StatusMessage = message;
                Log.Debug("Status message updated: {Message}", message);
            });
        }
        else
        {
            // Fallback - set directly (may not update UI)
            StatusMessage = message;
            Log.Warning("No dispatcher available, status message may not update UI: {Message}", message);
        }
    }

    [RelayCommand]
    private async Task LoadDisksAsync()
    {
        if (_diskEnumerator is not null)
            AvailableDisks = await _diskEnumerator.GetDisksAsync();
    }

    partial void OnSelectedTargetDiskChanged(DiskInfo? value)
    {
        _ = LoadTargetPartitionsAsync(value);
        OnPropertyChanged(nameof(CanStartRestore));
    }

    partial void OnSourceImagePathChanged(string value)
    {
        OnPropertyChanged(nameof(CanStartRestore));
        _ = LoadSourceSidecarAsync(value);
    }

    partial void OnIsRestoreInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartRestore));
    }

    private async Task LoadTargetPartitionsAsync(DiskInfo? disk)
    {
        if (_diskEnumerator is null || disk is null)
        {
            AvailableTargetPartitions = new List<PartitionInfo>();
            SelectedTargetPartition = null;
            HasTargetPartitions = false;
            return;
        }

        var partitions = await _diskEnumerator.GetPartitionsAsync(disk.DiskNumber);

        // Filter out MSR partitions
        var userPartitions = partitions.Where(p =>
            !string.Equals(p.PartitionType, "MSR", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Prepend "Entire Disk" sentinel
        var list = new List<PartitionInfo> { EntireDiskSentinel };
        list.AddRange(userPartitions);
        AvailableTargetPartitions = list;
        SelectedTargetPartition = EntireDiskSentinel;
        HasTargetPartitions = list.Count > 0;
    }

    private async Task LoadSourceSidecarAsync(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            SourceDisk = null;
            SourcePartitions = null;
            return;
        }

        try
        {
            var sidecar = await ImageSidecar.LoadAsync(imagePath);
            if (sidecar is not null)
            {
                var (disk, parts) = sidecar.ToDiskAndPartitions();
                SourceDisk = disk;
                SourcePartitions = parts;
                Log.Debug("Loaded sidecar for {Path}: {Count} partitions", imagePath, parts.Count);
            }
            else
            {
                SourceDisk = null;
                SourcePartitions = null;
                Log.Debug("No sidecar found for {Path}", imagePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load sidecar for {Path}", imagePath);
            SourceDisk = null;
            SourcePartitions = null;
        }
    }

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        var file = await App.RunOnUIThreadAsync(async () =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".vhdx");
            picker.FileTypeFilter.Add(".vhd");
            picker.FileTypeFilter.Add(".img");

            InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            return await picker.PickSingleFileAsync();
        });

        if (file is not null)
            SourceImagePath = file.Path;
    }

    [RelayCommand]
    private async Task StartRestoreAsync()
    {
        if (_restoreEngine is null)
        {
            StatusMessage = "Restore engine not available";
            return;
        }

        if (string.IsNullOrEmpty(SourceImagePath))
        {
            StatusMessage = "Please select a source image";
            return;
        }

        if (SelectedTargetDisk is null)
        {
            StatusMessage = "Please select a target disk";
            return;
        }

        // Build target path
        string targetPath;
        if (IsPartitionRestore)
        {
            targetPath = $"{SelectedTargetDisk.DiskNumber}:{SelectedTargetPartition!.PartitionNumber}";
        }
        else
        {
            targetPath = SelectedTargetDisk.DiskNumber.ToString();
        }

        // Create restore job
        var job = new RestoreJob
        {
            SourceImagePath = SourceImagePath,
            TargetPath = targetPath,
            VerifyDuringRestore = VerifyDuringRestore,
            ForceOverwrite = false // We'll validate first
        };

        // Validate first
        StatusMessage = "Validating restore configuration...";
        try
        {
            await _restoreEngine.ValidateRestoreAsync(job);
        }
        catch (InvalidOperationException ex)
        {
            var errorMsg = $"Validation failed: {ex.Message}";
            SetStatusMessageSafe(errorMsg);
            return;
        }
        catch (Exception ex)
        {
            var errorMsg = $"Validation error: {ex.Message}";
            SetStatusMessageSafe(errorMsg);
            return;
        }

        // Confirm with user if it's a system/boot disk
        if (SelectedTargetDisk.IsSystemDisk || SelectedTargetDisk.IsBootDisk)
        {
            // In a real UI, show a warning dialog here
            StatusMessage = "WARNING: Target is system/boot disk. Restore cancelled for safety.";
            return;
        }

        try
        {
            IsRestoreInProgress = true;
            ProgressPercentage = 0;
            BytesProcessed = 0;
            TotalBytes = 0;
            BytesPerSecond = 0;
            EstimatedTimeRemaining = string.Empty;
            StatusMessage = "Starting restore...";

            _cancellationTokenSource = new CancellationTokenSource();
            
            var lastUiUpdate = DateTime.MinValue;
            var progress = new Progress<OperationProgress>(p =>
            {
                var now = DateTime.UtcNow;
                if ((now - lastUiUpdate).TotalMilliseconds < 300 && p.PercentComplete < 100)
                    return;
                lastUiUpdate = now;

                ProgressPercentage = p.PercentComplete;
                BytesProcessed = p.BytesProcessed;
                TotalBytes = p.TotalBytes;
                BytesPerSecond = p.BytesPerSecond;

                var status = $"{p.StatusMessage} - {p.PercentComplete:F1}%";

                if (p.TotalBytes > 0)
                {
                    status += $" ({FormatBytes((ulong)p.BytesProcessed)} / {FormatBytes((ulong)p.TotalBytes)})";
                }

                if (p.BytesPerSecond > 0)
                {
                    status += $" - {FormatBytes((ulong)p.BytesPerSecond)}/s";

                    if (p.TimeRemaining.HasValue && p.TimeRemaining.Value.TotalSeconds > 0)
                    {
                        EstimatedTimeRemaining = FormatTimeSpan(p.TimeRemaining.Value);
                        status += $" - ETA: {EstimatedTimeRemaining}";
                    }
                }

                StatusMessage = status;
            });

            var progressReporter = new ProgressReporter(progress);
            await _restoreEngine.ExecuteAsync(job, progressReporter, _cancellationTokenSource.Token);

            StatusMessage = "Restore completed successfully!";
            ProgressPercentage = 100;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Restore cancelled";
            Log.Warning("Restore operation cancelled by user");
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
            Log.Error(ex, "Restore validation or operation failed");
        }
        catch (IOException ex)
        {
            StatusMessage = $"Restore I/O error: {ex.Message}";
            Log.Error(ex, "Restore I/O error");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
            Log.Error(ex, "Restore failed with unexpected error");
        }
        finally
        {
            IsRestoreInProgress = false;
            OnPropertyChanged(nameof(CanStartRestore));
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    private void CancelRestore()
    {
        _cancellationTokenSource?.Cancel();
        StatusMessage = "Cancelling restore...";
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
        if (ts.TotalSeconds < 60)
            return $"{ts.Seconds}s";
        if (ts.TotalMinutes < 60)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{(int)ts.TotalHours}h {ts.Minutes}m";
    }
}
