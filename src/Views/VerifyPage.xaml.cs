using Chronos.App.ViewModels;
using Chronos.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace Chronos.App.Views;

public sealed partial class VerifyPage : Page
{
    public VerifyViewModel ViewModel { get; }

    public VerifyPage()
    {
        ViewModel = App.Services.GetRequiredService<VerifyViewModel>();
        this.InitializeComponent();
        this.DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VerifyViewModel.SourceDisk) or nameof(VerifyViewModel.SourcePartitions))
        {
            SourceDiskMap.Disk = ViewModel.SourceDisk;
            SourceDiskMap.Partitions = ViewModel.SourcePartitions;
        }
    }

    private void OnBrowseImageClick(object sender, RoutedEventArgs e)
    {
        if (NativeFileDialog.TryPickOpenFile(
                App.MainWindowHandle,
                "Disk Images (*.vhdx;*.vhd;*.img)\0*.vhdx;*.vhd;*.img\0All files (*.*)\0*.*\0\0",
                out var path))
        {
            ViewModel.ImagePath = path ?? string.Empty;
        }
    }
}
