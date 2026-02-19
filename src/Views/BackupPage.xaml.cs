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
            try
            {
                Program.Log("  BackupPage.Loaded: IsWinPE=" + Chronos.Common.Helpers.PeEnvironment.IsWinPE
                    + ", HasWmi=" + Chronos.Common.Helpers.PeEnvironment.Capabilities.HasWmi);
                Program.Log("  BackupPage.Loaded: loading disks...");
                Program.FlushLog();
                if (ViewModel.AvailableDisks.Count == 0)
                    await ViewModel.LoadDisksCommand.ExecuteAsync(null);
                Program.Log("  BackupPage.Loaded: disks loaded OK (" + ViewModel.AvailableDisks.Count + " disks)");
                Program.FlushLog();

                // Post-load diagnostic: enqueue a deferred callback to verify the UI pump survives
                DispatcherQueue.TryEnqueue(() =>
                {
                    Program.Log("  [post-load] UI dispatcher alive after BackupPage.Loaded");
                    Program.FlushLog();
                });
            }
            catch (Exception ex)
            {
                Program.Log("  BackupPage.Loaded FAILED: " + ex.GetType().Name + ": " + ex.Message);
                Program.Log("  Stack: " + ex.StackTrace);
                if (ex.InnerException != null)
                    Program.Log("  Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                Program.FlushLog();
            }
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
