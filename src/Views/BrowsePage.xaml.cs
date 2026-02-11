using Chronos.App.ViewModels;
using Chronos.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace Chronos.App.Views;

public sealed partial class BrowsePage : Page
{
    public BrowseViewModel ViewModel { get; }

    public BrowsePage()
    {
        ViewModel = App.Services.GetRequiredService<BrowseViewModel>();
        this.InitializeComponent();
        this.DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateStatusDisplay();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowseViewModel.SourceDisk) or nameof(BrowseViewModel.SourcePartitions))
        {
            SourceDiskMap.Disk = ViewModel.SourceDisk;
            SourceDiskMap.Partitions = ViewModel.SourcePartitions;
        }

        if (e.PropertyName is nameof(BrowseViewModel.IsMounted) or nameof(BrowseViewModel.MountedDriveLetter))
        {
            UpdateStatusDisplay();
        }
    }

    private void UpdateStatusDisplay()
    {
        if (ViewModel.IsMounted && ViewModel.MountedDriveLetter is not null)
        {
            StatusIcon.Glyph = "\uE73E"; // Checkmark
            StatusIcon.Opacity = 1.0;
            StatusTitle.Text = $"Mounted to {ViewModel.MountedDriveLetter}:\\";
        }
        else
        {
            StatusIcon.Glyph = "\uEA8A"; // Disk icon
            StatusIcon.Opacity = 0.4;
            StatusTitle.Text = "No image mounted";
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
