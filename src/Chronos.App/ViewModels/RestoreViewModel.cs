using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.Core.Imaging;
using Chronos.Core.Models;
using Chronos.Core.Services;
using Chronos.Core.Progress;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.UI.Dispatching;

namespace Chronos.App.ViewModels;

public partial class RestoreViewModel : ObservableObject
{
    private readonly IRestoreEngine? _restoreEngine;
    private readonly IDiskEnumerator? _diskEnumerator;
    private CancellationTokenSource? _cancellationTokenSource;

    [ObservableProperty] public partial string SourceImagePath { get; set; } = string.Empty;
    [ObservableProperty] public partial List<DiskInfo> AvailableDisks { get; set; } = new();
    [ObservableProperty] public partial List<PartitionInfo> AvailableTargetPartitions { get; set; } = new();
    [ObservableProperty] public partial DiskInfo? SelectedTargetDisk { get; set; }
    [ObservableProperty] public partial PartitionInfo? SelectedTargetPartition { get; set; }
    [ObservableProperty] public partial bool VerifyDuringRestore { get; set; } = true;
    [ObservableProperty] public partial bool IsRestoreInProgress { get; set; }
    [ObservableProperty] public partial double ProgressPercentage { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasTargetPartitions { get; set; }
    [ObservableProperty] public partial long BytesProcessed { get; set; }
    [ObservableProperty] public partial long TotalBytes { get; set; }
    [ObservableProperty] public partial long BytesPerSecond { get; set; }
    [ObservableProperty] public partial string EstimatedTimeRemaining { get; set; } = string.Empty;

    public RestoreViewModel(IRestoreEngine? restoreEngine = null, IDiskEnumerator? diskEnumerator = null)
    {
        _restoreEngine = restoreEngine;
        _diskEnumerator = diskEnumerator;
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
        AvailableTargetPartitions = partitions;
        SelectedTargetPartition = null;
        HasTargetPartitions = partitions.Count > 0;
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
        if (SelectedTargetPartition is not null)
        {
            targetPath = $"{SelectedTargetDisk.DiskNumber}:{SelectedTargetPartition.PartitionNumber}";
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
        bool isValid = await _restoreEngine.ValidateRestoreAsync(job);
        
        if (!isValid)
        {
            StatusMessage = "Restore validation failed. Please check your source and target selections.";
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
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsRestoreInProgress = false;
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
