using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.Core.VirtualDisk;
using Chronos.Core.Models;
using Chronos.Core.Imaging;
using Serilog;

namespace Chronos.App.ViewModels;

public partial class BrowseViewModel : ObservableObject
{
    private readonly IVirtualDiskService? _virtualDiskService;

    [ObservableProperty] public partial string ImagePath { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsMounted { get; set; }
    [ObservableProperty] public partial char? MountedDriveLetter { get; set; }
    [ObservableProperty] public partial string? MountedFolderPath { get; set; }
    [ObservableProperty] public partial bool MountReadOnly { get; set; } = true;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;

    // Source image sidecar data for disk map display
    [ObservableProperty] public partial DiskInfo? SourceDisk { get; set; }
    [ObservableProperty] public partial List<PartitionInfo>? SourcePartitions { get; set; }

    public bool CanMount => !string.IsNullOrWhiteSpace(ImagePath) && !IsMounted;
    public bool CanDismount => IsMounted;
    public bool CanOpenExplorer => IsMounted && MountedDriveLetter is not null;

    public BrowseViewModel(IVirtualDiskService? virtualDiskService = null)
    {
        _virtualDiskService = virtualDiskService;
    }

    partial void OnImagePathChanged(string value)
    {
        OnPropertyChanged(nameof(CanMount));
        MountToDriveLetterCommand.NotifyCanExecuteChanged();
        _ = LoadSourceSidecarAsync(value);
    }

    partial void OnIsMountedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanMount));
        OnPropertyChanged(nameof(CanDismount));
        OnPropertyChanged(nameof(CanOpenExplorer));
        MountToDriveLetterCommand.NotifyCanExecuteChanged();
        DismountCommand.NotifyCanExecuteChanged();
        OpenInExplorerCommand.NotifyCanExecuteChanged();
    }

    partial void OnMountedDriveLetterChanged(char? value)
    {
        OnPropertyChanged(nameof(CanOpenExplorer));
        OpenInExplorerCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMount))]
    private async Task MountToDriveLetterAsync()
    {
        if (_virtualDiskService is null)
        {
            StatusMessage = "Virtual disk service not available";
            return;
        }

        if (string.IsNullOrEmpty(ImagePath))
        {
            StatusMessage = "Please select an image first";
            return;
        }

        try
        {
            StatusMessage = "Mounting image...";
            var driveLetter = await _virtualDiskService.MountToDriveLetterAsync(ImagePath, MountReadOnly);
            
            MountedDriveLetter = driveLetter;
            IsMounted = true;
            StatusMessage = $"Mounted to drive {driveLetter}:";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to mount: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanDismount))]
    private async Task DismountAsync()
    {
        if (_virtualDiskService is null)
        {
            StatusMessage = "Virtual disk service not available";
            return;
        }

        if (!IsMounted || string.IsNullOrEmpty(ImagePath))
        {
            StatusMessage = "No image is mounted";
            return;
        }

        try
        {
            StatusMessage = "Dismounting image...";
            await _virtualDiskService.DismountAsync(ImagePath);
            
            MountedDriveLetter = null;
            MountedFolderPath = null;
            IsMounted = false;
            StatusMessage = "Image dismounted successfully";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to dismount: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenExplorer))]
    private void OpenInExplorer()
    {
        if (MountedDriveLetter is null) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"{MountedDriveLetter}:\\",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open Explorer: {ex.Message}";
        }
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
                Log.Debug("Browse: Loaded sidecar for {Path}: {Count} partitions", imagePath, parts.Count);
            }
            else
            {
                SourceDisk = null;
                SourcePartitions = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Browse: Failed to load sidecar for {Path}", imagePath);
            SourceDisk = null;
            SourcePartitions = null;
        }
    }
}
