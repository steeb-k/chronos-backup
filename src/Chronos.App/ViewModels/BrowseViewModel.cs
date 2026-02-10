using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.Core.VirtualDisk;
using Windows.Storage.Pickers;
using WinRT.Interop;

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
    [ObservableProperty] public partial string ExtractionDestination { get; set; } = string.Empty;

    public BrowseViewModel(IVirtualDiskService? virtualDiskService = null)
    {
        _virtualDiskService = virtualDiskService;
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
        {
            ImagePath = file.Path;
            StatusMessage = $"Selected image: {file.Name}";
        }
    }

    [RelayCommand]
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

        if (IsMounted)
        {
            StatusMessage = "Image is already mounted";
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

    [RelayCommand]
    private async Task MountToFolderAsync()
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

        var folder = await App.RunOnUIThreadAsync(async () =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            return await picker.PickSingleFolderAsync();
        });

        if (folder is null)
            return;

        try
        {
            StatusMessage = "Mounting image to folder...";
            await _virtualDiskService.MountToFolderAsync(ImagePath, folder.Path, MountReadOnly);
            
            MountedFolderPath = folder.Path;
            IsMounted = true;
            StatusMessage = $"Mounted to folder: {folder.Path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to mount: {ex.Message}";
        }
    }

    [RelayCommand]
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

    [RelayCommand]
    private async Task ExtractFilesAsync()
    {
        if (!IsMounted || MountedDriveLetter is null)
        {
            StatusMessage = "Please mount the image first";
            return;
        }

        var folder = await App.RunOnUIThreadAsync(async () =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            return await picker.PickSingleFolderAsync();
        });

        if (folder is null)
            return;

        try
        {
            StatusMessage = "Extracting files...";
            ExtractionDestination = folder.Path;

            // Copy all files from mounted drive to destination
            string sourcePath = $"{MountedDriveLetter}:\\";
            await CopyDirectoryAsync(sourcePath, folder.Path);

            StatusMessage = $"Files extracted successfully to {folder.Path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to extract files: {ex.Message}";
        }
    }

    private async Task CopyDirectoryAsync(string sourceDir, string destDir)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            var files = Directory.GetFiles(sourceDir);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var destFile = Path.Combine(destDir, fileName);
                File.Copy(file, destFile, overwrite: true);
            }

            var dirs = Directory.GetDirectories(sourceDir);
            foreach (var dir in dirs)
            {
                var dirName = Path.GetFileName(dir);
                var destSubDir = Path.Combine(destDir, dirName);
                CopyDirectoryAsync(dir, destSubDir).GetAwaiter().GetResult();
            }
        });
    }
}
