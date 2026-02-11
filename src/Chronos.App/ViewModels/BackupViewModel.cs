using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.App.Services;
using Chronos.Core.Models;
using Chronos.Core.Services;
using System.Linq;
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
    [ObservableProperty] public partial int CompressionLevel { get; set; } = 3;
    [ObservableProperty] public partial bool UseVSS { get; set; } = true;
    [ObservableProperty] public partial bool VerifyAfterBackup { get; set; } = true;
    [ObservableProperty] public partial bool HasPartitions { get; set; }

    /// <summary>Sentinel value representing "Entire Disk" in the partition dropdown.</summary>
    public static readonly PartitionInfo EntireDiskSentinel = new()
    {
        DiskNumber = uint.MaxValue,
        PartitionNumber = uint.MaxValue,
        Size = 0,
        PartitionType = "Entire Disk"
    };

    /// <summary>True when a specific partition is selected (not Entire Disk).</summary>
    public bool IsPartitionBackup => SelectedPartition is not null && SelectedPartition != EntireDiskSentinel;

    public bool IsBackupInProgress => _operationsService?.IsBackupInProgress ?? false;
    public double ProgressPercentage => _operationsService?.ProgressPercentage ?? 0;
    public string StatusMessage => _operationsService?.StatusMessage ?? string.Empty;
    public bool CanStartBackup => SelectedDisk is not null && 
        !string.IsNullOrWhiteSpace(DestinationPath) && 
        !IsBackupInProgress;
    public string ProgressText => $"{ProgressPercentage:F1}%";

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
            var disks = await _diskEnumerator.GetDisksAsync();
            // Append refresh option
            disks.Add(DiskInfo.RefreshSentinel);
            AvailableDisks = disks;
        }
    }

    partial void OnSelectedDiskChanged(DiskInfo? value)
    {
        // Handle refresh sentinel selection
        if (value?.IsRefreshSentinel == true)
        {
            SelectedDisk = null;
            _ = LoadDisksAsync();
            return;
        }

        _ = LoadPartitionsForDiskAsync(value);
        OnPropertyChanged(nameof(CanStartBackup));
    }

    partial void OnSelectedPartitionChanged(PartitionInfo? value)
    {
        OnPropertyChanged(nameof(IsPartitionBackup));
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

        // Filter out MSR partitions — these are system infrastructure
        // and only meaningful as part of a full-disk backup (via the "Entire Disk" option).
        // EFI (ESP) partitions are kept as they can be useful to back up individually.
        var userPartitions = partitions.Where(p =>
            !string.Equals(p.PartitionType, "MSR", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Prepend "Entire Disk" sentinel
        var list = new List<PartitionInfo> { EntireDiskSentinel };
        list.AddRange(userPartitions);
        AvailablePartitions = list;
        SelectedPartition = EntireDiskSentinel;
        HasPartitions = list.Count > 0;
    }

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (_operationsService is null)
            return;

        if (SelectedDisk is null)
            return;

        if (string.IsNullOrWhiteSpace(DestinationPath))
            return;

        bool isPartition = IsPartitionBackup;
        var backupType = isPartition ? BackupType.Partition : BackupType.FullDisk;

        var job = new BackupJob
        {
            SourcePath = isPartition
                ? $"{SelectedDisk.DiskNumber}:{SelectedPartition!.PartitionNumber}"
                : $"\\\\.\\PhysicalDrive{SelectedDisk.DiskNumber}",
            DestinationPath = DestinationPath,
            Type = backupType,
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
