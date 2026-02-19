using Chronos.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Chronos.App.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Chronos.App.Views;

public sealed partial class RestorePage : Page
{
    public RestoreViewModel ViewModel { get; }

    public RestorePage()
    {
        ViewModel = App.Services.GetRequiredService<RestoreViewModel>();
        this.InitializeComponent();
        this.DataContext = ViewModel;
        
        // Update InfoBar severity when status changes
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        this.Loaded += async (s, e) =>
        {
            try
            {
                if (ViewModel.AvailableDisks.Count == 0)
                    await ViewModel.LoadDisksCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                Program.Log("  RestorePage.Loaded FAILED: " + ex.GetType().Name + ": " + ex.Message);
                Program.FlushLog();
            }
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RestoreViewModel.StatusMessage) or nameof(RestoreViewModel.IsStatusError))
        {
            if (ViewModel.IsStatusError)
                StatusInfoBar.Severity = InfoBarSeverity.Error;
            else if (ViewModel.StatusMessage.Contains("completed successfully", StringComparison.OrdinalIgnoreCase))
                StatusInfoBar.Severity = InfoBarSeverity.Success;
            else if (ViewModel.StatusMessage.StartsWith("Warning", StringComparison.OrdinalIgnoreCase))
                StatusInfoBar.Severity = InfoBarSeverity.Warning;
            else
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
        }

        if (e.PropertyName is nameof(RestoreViewModel.SelectedTargetDisk) or nameof(RestoreViewModel.AvailableTargetPartitions))
        {
            TargetDiskMap.Disk = ViewModel.SelectedTargetDisk;
            // Filter out the "Entire Disk" sentinel from the visual disk map
            TargetDiskMap.Partitions = ViewModel.AvailableTargetPartitions
                .Where(p => p != RestoreViewModel.EntireDiskSentinel).ToList();
        }

        if (e.PropertyName is nameof(RestoreViewModel.SelectedTargetPartition))
        {
            // Highlight the selected partition on the target disk map
            TargetDiskMap.HighlightedPartition = ViewModel.IsPartitionRestore
                ? ViewModel.SelectedTargetPartition : null;
        }

        if (e.PropertyName is nameof(RestoreViewModel.SourceDisk) or nameof(RestoreViewModel.SourcePartitions))
        {
            SourceDiskMap.Disk = ViewModel.SourceDisk;
            SourceDiskMap.Partitions = ViewModel.SourcePartitions;
        }

        if (e.PropertyName is nameof(RestoreViewModel.SelectedSourcePartition))
        {
            // Highlight the selected source partition on the source disk map
            SourceDiskMap.HighlightedPartition = ViewModel.IsSinglePartitionRestore
                ? ViewModel.SelectedSourcePartition : null;
        }
    }

    private void OnBrowseImageClick(object sender, RoutedEventArgs e)
    {
        if (NativeFileDialog.TryPickOpenFile(
                App.MainWindowHandle,
                "Disk Images (*.vhdx;*.vhd;*.img)\0*.vhdx;*.vhd;*.img\0All files (*.*)\0*.*\0\0",
                out var path))
        {
            ViewModel.SourceImagePath = path ?? string.Empty;
        }
    }

    private async Task ShowPickerErrorAsync(Exception ex)
    {
        var details = $"{ex.GetType().FullName}\nHResult: 0x{ex.HResult:X8}\n\n{ex}";
        var dialog = new ContentDialog
        {
            Title = "Browse failed",
            Content = details,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };

        await dialog.ShowAsync();
    }
}
