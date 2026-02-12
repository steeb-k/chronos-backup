using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.App.Services;
using Chronos.Core.Models;
using Chronos.Core.Services;
using Chronos.Common.Extensions;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Chronos.App.ViewModels;

/// <summary>
/// Represents a destination drive for backup storage.
/// </summary>
public partial class TargetDriveInfo
{
    public string DriveLetter { get; set; } = string.Empty;
    public string? VolumeLabel { get; set; }
    public long FreeSpaceBytes { get; set; }
    public long TotalSizeBytes { get; set; }

    public override string ToString()
    {
        var label = string.IsNullOrEmpty(VolumeLabel) ? "Local Disk" : VolumeLabel;
        var freeSize = FreeSpaceBytes.ToHumanReadableSize();
        return $"{DriveLetter} - {label}, {freeSize} free";
    }
}

public partial class BackupViewModel : ObservableObject
{
    private readonly IBackupOperationsService? _operationsService;
    private readonly IDiskEnumerator? _diskEnumerator;

    [ObservableProperty] public partial List<DiskInfo> AvailableDisks { get; set; } = new();
    [ObservableProperty] public partial List<PartitionInfo> AvailablePartitions { get; set; } = new();
    [ObservableProperty] public partial List<TargetDriveInfo> AvailableTargetDrives { get; set; } = new();
    [ObservableProperty] public partial DiskInfo? SelectedDisk { get; set; }
    [ObservableProperty] public partial PartitionInfo? SelectedPartition { get; set; }
    [ObservableProperty] public partial TargetDriveInfo? SelectedTargetDrive { get; set; }
    [ObservableProperty] public partial string BackupName { get; set; } = string.Empty;
    [ObservableProperty] public partial int CompressionLevel { get; set; } = 3;
    [ObservableProperty] public partial bool UseVSS { get; set; } = true;
    [ObservableProperty] public partial bool VerifyAfterBackup { get; set; } = true;
    [ObservableProperty] public partial bool HasPartitions { get; set; }

    // Tracks the backup configuration when backup started (for display during operation)
    [ObservableProperty] public partial string? ActiveBackupSourceDescription { get; set; }
    [ObservableProperty] public partial string? ActiveBackupDestinationPath { get; set; }

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
    
    /// <summary>True when backup is in progress - used to lock/hide input fields.</summary>
    public bool IsInputLocked => IsBackupInProgress;
    
    /// <summary>Inverse of IsInputLocked for binding IsEnabled.</summary>
    public bool IsInputEnabled => !IsInputLocked;
    
    public bool CanStartBackup => SelectedDisk is not null && 
        !string.IsNullOrWhiteSpace(BackupName) && 
        SelectedTargetDrive is not null &&
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
                OnPropertyChanged(nameof(IsInputLocked));
                OnPropertyChanged(nameof(IsInputEnabled));
            };
        }
    }

    [RelayCommand]
    private async Task LoadDisksAsync()
    {
        if (_diskEnumerator is not null)
        {
            // Force full re-enumeration so the refresh button picks up
            // any disk/partition changes.
            await _diskEnumerator.RefreshAsync();
            AvailableDisks = await _diskEnumerator.GetDisksAsync();

            // Reset selection so partition list & disk map re-draw
            SelectedDisk = null;
        }
        LoadTargetDrives();
    }

    [RelayCommand]
    private void LoadTargetDrives()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => new TargetDriveInfo
            {
                DriveLetter = d.Name.TrimEnd('\\'),
                VolumeLabel = d.VolumeLabel,
                FreeSpaceBytes = d.AvailableFreeSpace,
                TotalSizeBytes = d.TotalSize
            })
            .OrderBy(d => d.DriveLetter)
            .ToList();
        
        AvailableTargetDrives = drives;
        
        // Auto-select first non-C drive if available, otherwise first drive
        SelectedTargetDrive = drives.FirstOrDefault(d => !d.DriveLetter.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                              ?? drives.FirstOrDefault();
    }

    partial void OnSelectedDiskChanged(DiskInfo? value)
    {
        _ = LoadPartitionsForDiskAsync(value);
        OnPropertyChanged(nameof(CanStartBackup));
    }

    partial void OnSelectedPartitionChanged(PartitionInfo? value)
    {
        OnPropertyChanged(nameof(IsPartitionBackup));
        OnPropertyChanged(nameof(CanStartBackup));
    }

    partial void OnBackupNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanStartBackup));
    }

    partial void OnSelectedTargetDriveChanged(TargetDriveInfo? value)
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

        if (SelectedDisk is null || SelectedTargetDrive is null || string.IsNullOrWhiteSpace(BackupName))
            return;

        bool isPartition = IsPartitionBackup;
        var backupType = isPartition ? BackupType.Partition : BackupType.FullDisk;

        // Build the destination path: Drive:\BackupName\{serial or GUID}.vhdx
        string sanitizedBackupName = SanitizeFileName(BackupName);
        string backupFolder = Path.Combine(SelectedTargetDrive.DriveLetter + "\\", sanitizedBackupName);
        
        // Use serial number if available, otherwise use disk number as fallback
        string diskIdentifier = !string.IsNullOrWhiteSpace(SelectedDisk.SerialNumber) 
            ? SanitizeFileName(SelectedDisk.SerialNumber)
            : $"Disk{SelectedDisk.DiskNumber}";
        
        // Add timestamp for uniqueness
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string fileName = $"{diskIdentifier}_{timestamp}.vhdx";
        string destinationPath = Path.Combine(backupFolder, fileName);

        // Create the backup folder
        Directory.CreateDirectory(backupFolder);

        // Store active backup info for display during operation
        ActiveBackupSourceDescription = isPartition 
            ? $"Disk {SelectedDisk.DiskNumber}, Partition {SelectedPartition!.PartitionNumber}"
            : $"Disk {SelectedDisk.DiskNumber} ({SelectedDisk.Model})";
        ActiveBackupDestinationPath = destinationPath;

        var job = new BackupJob
        {
            SourcePath = isPartition
                ? $"{SelectedDisk.DiskNumber}:{SelectedPartition!.PartitionNumber}"
                : $"\\\\.\\PhysicalDrive{SelectedDisk.DiskNumber}",
            DestinationPath = destinationPath,
            Type = backupType,
            CompressionLevel = CompressionLevel,
            UseVSS = UseVSS
        };

        await _operationsService.StartBackupAsync(job, VerifyAfterBackup);
    }

    /// <summary>
    /// Removes invalid filename characters from a string.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
        // Also replace spaces with underscores for cleaner paths
        return Regex.Replace(sanitized.Trim(), @"\s+", "_");
    }

    [RelayCommand]
    private void CancelBackup()
    {
        _operationsService?.CancelBackup();
    }
}
