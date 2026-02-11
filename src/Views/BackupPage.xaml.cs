using Chronos.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;

namespace Chronos.App.Views;

public sealed partial class BackupPage : Page
{
    public BackupViewModel ViewModel { get; }

    public BackupPage()
    {
        ViewModel = App.Services.GetRequiredService<BackupViewModel>();
        this.InitializeComponent();
        this.DataContext = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        this.Loaded += async (s, e) =>
        {
            if (ViewModel.AvailableDisks.Count == 0)
                await ViewModel.LoadDisksCommand.ExecuteAsync(null);
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BackupViewModel.SelectedDisk) or nameof(BackupViewModel.AvailablePartitions))
        {
            SourceDiskMap.Disk = ViewModel.SelectedDisk;
            // Filter out the "Entire Disk" sentinel from the partition map
            SourceDiskMap.Partitions = ViewModel.AvailablePartitions
                .Where(p => p != BackupViewModel.EntireDiskSentinel)
                .ToList();
        }

        if (e.PropertyName is nameof(BackupViewModel.SelectedPartition) or nameof(BackupViewModel.IsPartitionBackup))
        {
            // Highlight the selected partition (null when Entire Disk)
            SourceDiskMap.HighlightedPartition = ViewModel.IsPartitionBackup ? ViewModel.SelectedPartition : null;
        }
    }
}
