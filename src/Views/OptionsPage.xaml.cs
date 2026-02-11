using Chronos.App.ViewModels;
using Chronos.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Chronos.App.Views;

public sealed partial class OptionsPage : Page
{
    public OptionsViewModel ViewModel { get; }

    public OptionsPage()
    {
        ViewModel = App.Services.GetRequiredService<OptionsViewModel>();
        this.InitializeComponent();
        this.DataContext = ViewModel;

        this.Loaded += (_, _) => ViewModel.ApplyTheme();
    }

    private void OnBrowseBackupFolderClick(object sender, RoutedEventArgs e)
    {
        if (NativeFileDialog.TryPickFolder(App.MainWindowHandle, out var path) && path is not null)
        {
            ViewModel.DefaultBackupPath = path;
        }
    }
}
