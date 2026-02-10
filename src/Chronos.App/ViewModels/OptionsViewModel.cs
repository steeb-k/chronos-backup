using CommunityToolkit.Mvvm.ComponentModel;

namespace Chronos.App.ViewModels;

public partial class OptionsViewModel : ObservableObject
{
    [ObservableProperty] public partial int DefaultCompressionLevel { get; set; } = 3;
    [ObservableProperty] public partial string DefaultBackupPath { get; set; } = string.Empty;
    [ObservableProperty] public partial bool UseVssByDefault { get; set; } = true;
    [ObservableProperty] public partial bool VerifyByDefault { get; set; } = true;
    [ObservableProperty] public partial bool UseDarkTheme { get; set; } = true;

    public OptionsViewModel()
    {
    }
}
