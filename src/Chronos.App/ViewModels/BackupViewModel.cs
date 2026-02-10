using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.App.Services;
using Chronos.Core.Models;
using Chronos.Core.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Chronos.App.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private readonly IBackupOperationsService? _operationsService;
    private readonly IDiskEnumerator? _diskEnumerator;

    [ObservableProperty] public partial List<DiskInfo> AvailableDisks { get; set; } = new();
    [ObservableProperty] public partial List<PartitionInfo> AvailablePartitions { get; set; } = new();
    [ObservableProperty] public partial DiskInfo? SelectedDisk { get; set; }
    [ObservableProperty] public partial PartitionInfo? SelectedPartition { get; set; }
    [ObservableProperty] public partial string DestinationPath { get; set; } = string.Empty;
    [ObservableProperty] public partial BackupType SelectedBackupType { get; set; } = BackupType.FullDisk;
    [ObservableProperty] public partial int CompressionLevel { get; set; } = 3;
    [ObservableProperty] public partial bool UseVSS { get; set; } = true;
    [ObservableProperty] public partial bool VerifyAfterBackup { get; set; } = true;
    [ObservableProperty] public partial bool HasPartitions { get; set; }

    // For clone operations
    [ObservableProperty] public partial List<DiskInfo> AvailableDestinationDisks { get; set; } = new();
    [ObservableProperty] public partial List<PartitionInfo> AvailableDestinationPartitions { get; set; } = new();
    [ObservableProperty] public partial DiskInfo? SelectedDestinationDisk { get; set; }
    [ObservableProperty] public partial PartitionInfo? SelectedDestinationPartition { get; set; }
    [ObservableProperty] public partial bool HasDestinationPartitions { get; set; }

    public bool IsBackupInProgress => _operationsService?.IsBackupInProgress ?? false;
    public double ProgressPercentage => _operationsService?.ProgressPercentage ?? 0;
    public string StatusMessage => _operationsService?.StatusMessage ?? string.Empty;
    public bool CanStartBackup => SelectedDisk is not null && 
        (IsCloneOperation ? SelectedDestinationDisk is not null : !string.IsNullOrWhiteSpace(DestinationPath)) && 
        !IsBackupInProgress;
    public string ProgressText => $"{ProgressPercentage:F1}%";
    public bool IsCloneOperation => SelectedBackupType == BackupType.DiskClone || SelectedBackupType == BackupType.PartitionClone;

    public BackupViewModel(IBackupOperationsService? operationsService = null, IDiskEnumerator? diskEnumerator = null)
    {
        _operationsService = operationsService;
        _diskEnumerator = diskEnumerator;

        if (_operationsService is ObservableObject observable)
        {
            observable.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(IsBackupInProgress));
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(CanStartBackup));
            };
        }
    }

    [RelayCommand]
    private async Task LoadDisksAsync()
    {
        if (_diskEnumerator is not null)
        {
            AvailableDisks = await _diskEnumerator.GetDisksAsync();
            AvailableDestinationDisks = await _diskEnumerator.GetDisksAsync();
        }
    }

    partial void OnSelectedDiskChanged(DiskInfo? value)
    {
        _ = LoadPartitionsForDiskAsync(value);
        OnPropertyChanged(nameof(CanStartBackup));
    }

    partial void OnSelectedDestinationDiskChanged(DiskInfo? value)
    {
        _ = LoadDestinationPartitionsAsync(value);
        OnPropertyChanged(nameof(CanStartBackup));
    }

    partial void OnSelectedBackupTypeChanged(BackupType value)
    {
        OnPropertyChanged(nameof(IsCloneOperation));
        OnPropertyChanged(nameof(CanStartBackup));
    }

    partial void OnDestinationPathChanged(string value)
    {
        OnPropertyChanged(nameof(CanStartBackup));
    }

    private async Task LoadPartitionsForDiskAsync(DiskInfo? disk)
    {
        if (_diskEnumerator is null || disk is null)
        {
            AvailablePartitions = new List<PartitionInfo>();
            SelectedPartition = null;
            HasPartitions = false;
            return;
        }

        var partitions = await _diskEnumerator.GetPartitionsAsync(disk.DiskNumber);
        AvailablePartitions = partitions;
        SelectedPartition = null;
        HasPartitions = partitions.Count > 0;
    }

    private async Task LoadDestinationPartitionsAsync(DiskInfo? disk)
    {
        if (_diskEnumerator is null || disk is null)
        {
            AvailableDestinationPartitions = new List<PartitionInfo>();
            SelectedDestinationPartition = null;
            HasDestinationPartitions = false;
            return;
        }

        var partitions = await _diskEnumerator.GetPartitionsAsync(disk.DiskNumber);
        AvailableDestinationPartitions = partitions;
        SelectedDestinationPartition = null;
        HasDestinationPartitions = partitions.Count > 0;
    }

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (_operationsService is null)
            return;

        if (SelectedDisk is null && SelectedPartition is null)
            return;

        // Build destination path based on operation type
        string destinationPath;
        if (IsCloneOperation)
        {
            // For clone operations, destination is a physical disk/partition
            if (SelectedDestinationDisk is null)
                return;

            if (SelectedBackupType == BackupType.PartitionClone && SelectedDestinationPartition is not null)
            {
                destinationPath = $"{SelectedDestinationDisk.DiskNumber}:{SelectedDestinationPartition.PartitionNumber}";
            }
            else
            {
                destinationPath = $"{SelectedDestinationDisk.DiskNumber}";
            }
        }
        else
        {
            // For backup operations, destination is a file path
            if (string.IsNullOrWhiteSpace(DestinationPath))
                return;
            destinationPath = DestinationPath;
        }

        var job = new BackupJob
        {
            SourcePath = SelectedBackupType == BackupType.FullDisk || SelectedBackupType == BackupType.DiskClone
                ? $"\\\\.\\PhysicalDrive{SelectedDisk?.DiskNumber}"
                : $"{SelectedDisk?.DiskNumber}:{SelectedPartition?.PartitionNumber}",
            DestinationPath = destinationPath,
            Type = SelectedBackupType,
            CompressionLevel = CompressionLevel,
            UseVSS = UseVSS
        };

        await _operationsService.StartBackupAsync(job, VerifyAfterBackup);
    }

    [RelayCommand]
    private void CancelBackup()
    {
        _operationsService?.CancelBackup();
    }

    [RelayCommand]
    private async Task BrowseDestinationAsync()
    {
        var file = await App.RunOnUIThreadAsync(async () =>
        {
            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("VHDX Image", new List<string> { ".vhdx" });
            picker.FileTypeChoices.Add("Raw Image", new List<string> { ".img" });
            picker.SuggestedFileName = "disk-image";

            InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            return await picker.PickSaveFileAsync();
        });

        if (file is not null)
            DestinationPath = file.Path;
    }
}
