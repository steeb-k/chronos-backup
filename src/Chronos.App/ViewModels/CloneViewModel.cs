using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.App.Services;
using Chronos.Core.Models;
using Chronos.Core.Services;
using System.Linq;

namespace Chronos.App.ViewModels;

public partial class CloneViewModel : ObservableObject
{
    private readonly IBackupOperationsService? _operationsService;
    private readonly IDiskEnumerator? _diskEnumerator;

    // Source
    [ObservableProperty] public partial List<DiskInfo> AvailableDisks { get; set; } = new();
    [ObservableProperty] public partial List<PartitionInfo> AvailablePartitions { get; set; } = new();
    [ObservableProperty] public partial DiskInfo? SelectedDisk { get; set; }
    [ObservableProperty] public partial PartitionInfo? SelectedPartition { get; set; }
    [ObservableProperty] public partial bool HasPartitions { get; set; }

    // Destination
    [ObservableProperty] public partial List<DiskInfo> AvailableDestinationDisks { get; set; } = new();
    [ObservableProperty] public partial List<PartitionInfo> AvailableDestinationPartitions { get; set; } = new();
    [ObservableProperty] public partial DiskInfo? SelectedDestinationDisk { get; set; }
    [ObservableProperty] public partial PartitionInfo? SelectedDestinationPartition { get; set; }
    [ObservableProperty] public partial bool HasDestinationPartitions { get; set; }

    /// <summary>Sentinel value representing "Entire Disk" in the source partition dropdown.</summary>
    public static readonly PartitionInfo EntireDiskSentinel = new()
    {
        DiskNumber = uint.MaxValue,
        PartitionNumber = uint.MaxValue,
        Size = 0,
        PartitionType = "Entire Disk"
    };

    /// <summary>True when a specific partition is selected (not Entire Disk).</summary>
    public bool IsPartitionClone => SelectedPartition is not null && SelectedPartition != EntireDiskSentinel;

    public bool IsCloneInProgress => _operationsService?.IsBackupInProgress ?? false;
    public double ProgressPercentage => _operationsService?.ProgressPercentage ?? 0;
    public string StatusMessage => _operationsService?.StatusMessage ?? string.Empty;
    public string ProgressText => $"{ProgressPercentage:F1}%";
    public bool CanStartClone => SelectedDisk is not null &&
        SelectedDestinationDisk is not null &&
        !IsCloneInProgress;

    public CloneViewModel(IBackupOperationsService? operationsService = null, IDiskEnumerator? diskEnumerator = null)
    {
        _operationsService = operationsService;
        _diskEnumerator = diskEnumerator;

        if (_operationsService is ObservableObject observable)
        {
            observable.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(IsCloneInProgress));
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(CanStartClone));
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

            var destDisks = await _diskEnumerator.GetDisksAsync();
            destDisks.Add(DiskInfo.RefreshSentinel);
            AvailableDestinationDisks = destDisks;
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
        OnPropertyChanged(nameof(CanStartClone));
    }

    partial void OnSelectedPartitionChanged(PartitionInfo? value)
    {
        OnPropertyChanged(nameof(IsPartitionClone));
        OnPropertyChanged(nameof(CanStartClone));
    }

    partial void OnSelectedDestinationDiskChanged(DiskInfo? value)
    {
        // Handle refresh sentinel selection
        if (value?.IsRefreshSentinel == true)
        {
            SelectedDestinationDisk = null;
            _ = LoadDisksAsync();
            return;
        }

        _ = LoadDestinationPartitionsAsync(value);
        OnPropertyChanged(nameof(CanStartClone));
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

        // Filter out MSR partitions
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

        // Filter out MSR partitions
        var userPartitions = partitions.Where(p =>
            !string.Equals(p.PartitionType, "MSR", StringComparison.OrdinalIgnoreCase))
            .ToList();

        AvailableDestinationPartitions = userPartitions;
        SelectedDestinationPartition = null;
        HasDestinationPartitions = userPartitions.Count > 0;
    }

    [RelayCommand]
    private async Task StartCloneAsync()
    {
        if (_operationsService is null || SelectedDisk is null || SelectedDestinationDisk is null)
            return;

        bool isPartition = IsPartitionClone;
        var backupType = isPartition ? BackupType.PartitionClone : BackupType.DiskClone;

        string sourcePath = isPartition
            ? $"{SelectedDisk.DiskNumber}:{SelectedPartition!.PartitionNumber}"
            : $"\\\\.\\PhysicalDrive{SelectedDisk.DiskNumber}";

        string destPath;
        if (isPartition && SelectedDestinationPartition is not null)
            destPath = $"{SelectedDestinationDisk.DiskNumber}:{SelectedDestinationPartition.PartitionNumber}";
        else
            destPath = $"{SelectedDestinationDisk.DiskNumber}";

        var job = new BackupJob
        {
            SourcePath = sourcePath,
            DestinationPath = destPath,
            Type = backupType,
            CompressionLevel = 0,
            UseVSS = false
        };

        await _operationsService.StartBackupAsync(job, verifyAfterBackup: false);
    }

    [RelayCommand]
    private void CancelClone()
    {
        _operationsService?.CancelBackup();
    }
}
