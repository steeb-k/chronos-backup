using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.Core.Imaging;
using Chronos.Core.Models;
using Chronos.Core.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Chronos.App.ViewModels;

public partial class RestoreViewModel : ObservableObject
{
    private readonly IRestoreEngine? _restoreEngine;
    private readonly IDiskEnumerator? _diskEnumerator;

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
    [RelayCommand] private async Task StartRestoreAsync() { await Task.CompletedTask; }
}
