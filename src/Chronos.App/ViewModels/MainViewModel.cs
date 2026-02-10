using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chronos.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "Chronos";
    [ObservableProperty] public partial object? CurrentPage { get; set; }

    public MainViewModel() { }

    [RelayCommand] private void NavigateToBackup() { }
    [RelayCommand] private void NavigateToRestore() { }
    [RelayCommand] private void NavigateToVerify() { }
    [RelayCommand] private void NavigateToBrowse() { }
    [RelayCommand] private void NavigateToOptions() { }
}
