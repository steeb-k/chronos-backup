using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.App.Services;

namespace Chronos.App.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly IOperationHistoryService? _historyService;

    [ObservableProperty] public partial List<OperationHistoryEntry> HistoryEntries { get; set; } = new();

    public HistoryViewModel(IOperationHistoryService? historyService = null)
    {
        _historyService = historyService;
        LoadHistory();
    }

    [RelayCommand]
    private void LoadHistory()
    {
        if (_historyService is null)
            return;

        HistoryEntries = _historyService.GetHistory();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (_historyService is null)
            return;

        _historyService.ClearHistory();
        HistoryEntries = new List<OperationHistoryEntry>();
    }

    [RelayCommand]
    private void RefreshHistory()
    {
        LoadHistory();
    }
}
