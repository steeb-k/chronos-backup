using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.Core.VirtualDisk;

namespace Chronos.App.ViewModels;

public partial class BrowseViewModel : ObservableObject
{
    private readonly IVirtualDiskService? _virtualDiskService;

    [ObservableProperty] public partial string ImagePath { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsMounted { get; set; }
    [ObservableProperty] public partial char? MountedDriveLetter { get; set; }
    [ObservableProperty] public partial string? MountedFolderPath { get; set; }
    [ObservableProperty] public partial bool MountReadOnly { get; set; } = true;

    public BrowseViewModel(IVirtualDiskService? virtualDiskService = null)
    {
        _virtualDiskService = virtualDiskService;
    }

    [RelayCommand] private async Task BrowseImageAsync() { await Task.CompletedTask; }
    [RelayCommand] private async Task MountToDriveLetterAsync() { await Task.CompletedTask; }
    [RelayCommand] private async Task MountToFolderAsync() { await Task.CompletedTask; }
    [RelayCommand] private async Task DismountAsync() { await Task.CompletedTask; }
}
