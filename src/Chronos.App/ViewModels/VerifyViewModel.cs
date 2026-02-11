using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.Core.Imaging;
using Chronos.Core.Models;
using Chronos.Core.Progress;
using Serilog;

namespace Chronos.App.ViewModels;

public partial class VerifyViewModel : ObservableObject
{
    private readonly IVerificationEngine? _verificationEngine;

    [ObservableProperty] public partial string ImagePath { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsVerificationInProgress { get; set; }
    [ObservableProperty] public partial bool IsHashInProgress { get; set; }

    public bool CanVerify => !string.IsNullOrWhiteSpace(ImagePath) && !IsVerificationInProgress && !IsHashInProgress;
    [ObservableProperty] public partial double ProgressPercentage { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool? VerificationResult { get; set; }
    [ObservableProperty] public partial string? ComputedHash { get; set; }
    [ObservableProperty] public partial bool IsVerifyProgressVisible { get; set; }
    [ObservableProperty] public partial bool IsHashProgressVisible { get; set; }
    [ObservableProperty] public partial bool IsVerifyResultVisible { get; set; }
    [ObservableProperty] public partial bool IsHashOutputVisible { get; set; }

    // Source image sidecar data for disk map display
    [ObservableProperty] public partial DiskInfo? SourceDisk { get; set; }
    [ObservableProperty] public partial List<PartitionInfo>? SourcePartitions { get; set; }

    public VerifyViewModel(IVerificationEngine? verificationEngine = null)
    {
        _verificationEngine = verificationEngine;
    }

    partial void OnImagePathChanged(string value)
    {
        NotifyCommandsCanExecute();
        _ = LoadSourceSidecarAsync(value);
    }
    partial void OnIsVerificationInProgressChanged(bool value) => NotifyCommandsCanExecute();
    partial void OnIsHashInProgressChanged(bool value) => NotifyCommandsCanExecute();

    private void NotifyCommandsCanExecute()
    {
        OnPropertyChanged(nameof(CanVerify));
        StartVerificationCommand.NotifyCanExecuteChanged();
        ComputeHashCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private async Task BrowseImageAsync() { await Task.CompletedTask; }

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task StartVerificationAsync()
    {
        if (_verificationEngine is null || string.IsNullOrWhiteSpace(ImagePath))
            return;

        try
        {
            IsVerificationInProgress = true;
            IsVerifyProgressVisible = true;
            IsVerifyResultVisible = false;
            VerificationResult = null;
            ProgressPercentage = 0;
            StatusMessage = "Verifying image...";

            var progress = new Progress<OperationProgress>(p =>
            {
                ProgressPercentage = p.PercentComplete;
                StatusMessage = p.StatusMessage ?? $"Verifying - {p.PercentComplete:F1}%";
            });
            var reporter = new VerificationProgressReporter(progress);

            var passed = await _verificationEngine.VerifyImageAsync(ImagePath, reporter);

            VerificationResult = passed;
            IsVerifyResultVisible = true;
            StatusMessage = passed ? "Verification passed." : "Verification failed.";
        }
        catch (Exception ex)
        {
            VerificationResult = false;
            IsVerifyResultVisible = true;
            StatusMessage = $"Verification failed: {ex.Message}";
        }
        finally
        {
            IsVerificationInProgress = false;
            IsVerifyProgressVisible = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task ComputeHashAsync()
    {
        if (_verificationEngine is null || string.IsNullOrWhiteSpace(ImagePath))
            return;

        try
        {
            IsHashInProgress = true;
            IsHashProgressVisible = true;
            IsHashOutputVisible = false;
            ComputedHash = null;
            StatusMessage = "Computing hash...";

            var hash = await _verificationEngine.ComputeHashAsync(ImagePath);

            ComputedHash = hash;
            IsHashOutputVisible = true;
            StatusMessage = "Hash computed.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Hash computation failed: {ex.Message}";
        }
        finally
        {
            IsHashInProgress = false;
            IsHashProgressVisible = false;
        }
    }

    private sealed class VerificationProgressReporter : IProgressReporter
    {
        private readonly IProgress<OperationProgress> _progress;

        public VerificationProgressReporter(IProgress<OperationProgress> progress) => _progress = progress;

        public void Report(OperationProgress progress) => _progress.Report(progress);
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
                Log.Debug("Verify: Loaded sidecar for {Path}: {Count} partitions", imagePath, parts.Count);
            }
            else
            {
                SourceDisk = null;
                SourcePartitions = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Verify: Failed to load sidecar for {Path}", imagePath);
            SourceDisk = null;
            SourcePartitions = null;
        }
    }
}
