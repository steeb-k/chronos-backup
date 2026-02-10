using Chronos.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Chronos.App.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Chronos.App.Views;

public sealed partial class BackupPage : Page
{
    public BackupViewModel ViewModel { get; }

    public BackupPage()
    {
        ViewModel = App.Services.GetRequiredService<BackupViewModel>();
        this.InitializeComponent();
        this.DataContext = ViewModel;
        
        this.Loaded += async (s, e) =>
        {
            if (ViewModel.AvailableDisks.Count == 0)
                await ViewModel.LoadDisksCommand.ExecuteAsync(null);
        };
    }

    private void OnBrowseDestinationClick(object sender, RoutedEventArgs e)
    {
        if (NativeFileDialog.TryPickSaveFile(
                App.MainWindowHandle,
                "VHDX Image (*.vhdx)\0*.vhdx\0Raw Image (*.img)\0*.img\0All files (*.*)\0*.*\0\0",
                "vhdx",
                "disk-image",
                out var path))
        {
            ViewModel.DestinationPath = path ?? string.Empty;
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
