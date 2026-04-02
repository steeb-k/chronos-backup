using Chronos.App.Helpers;
using Chronos.App.ViewModels;
using Chronos.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using System.Linq;

namespace Chronos.App.Views;

public sealed partial class ClonePage : Page
{
    private bool _wasCloneInProgress;

    public CloneViewModel ViewModel { get; }

    public ClonePage()
    {
        ViewModel = App.Services.GetRequiredService<CloneViewModel>();
        this.InitializeComponent();
        this.DataContext = ViewModel;

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
                Program.Log("  ClonePage.Loaded FAILED: " + ex.GetType().Name + ": " + ex.Message);
                Program.FlushLog();
            }
        };
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloneViewModel.IsCloneInProgress))
        {
            if (ViewModel.IsCloneInProgress)
                _wasCloneInProgress = true;
            else if (_wasCloneInProgress)
            {
                _wasCloneInProgress = false;
                var msg = ViewModel.StatusMessage;
                if (!string.IsNullOrEmpty(msg))
                    await CompletionDialogHelper.ShowCompletionDialogAsync(this.Content.XamlRoot, msg);
            }
        }

        if (e.PropertyName is nameof(CloneViewModel.SelectedDisk) or nameof(CloneViewModel.AvailablePartitions))
        {
            SourceDiskMap.Disk = ViewModel.SelectedDisk;
            // Filter out the "Entire Disk" sentinel from the visual disk map
            SourceDiskMap.Partitions = ViewModel.AvailablePartitions
                .Where(p => p != CloneViewModel.EntireDiskSentinel).ToList();
        }

        if (e.PropertyName is nameof(CloneViewModel.SelectedPartition))
        {
            // Highlight the selected partition on the source disk map
            SourceDiskMap.HighlightedPartition = ViewModel.IsPartitionClone
                ? ViewModel.SelectedPartition : null;
        }

        if (e.PropertyName is nameof(CloneViewModel.SelectedDestinationDisk) or nameof(CloneViewModel.AvailableDestinationPartitions))
        {
            DestDiskMap.Disk = ViewModel.SelectedDestinationDisk;
            DestDiskMap.Partitions = ViewModel.AvailableDestinationPartitions;
        }

        if (e.PropertyName is nameof(CloneViewModel.SelectedDestinationPartition))
        {
            // Highlight the selected partition on the destination disk map
            DestDiskMap.HighlightedPartition = ViewModel.IsPartitionClone && ViewModel.SelectedDestinationPartition is not null
                ? ViewModel.SelectedDestinationPartition : null;
        }
    }
}
