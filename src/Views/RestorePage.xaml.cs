using Chronos.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Chronos.App.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Chronos.App.Views;

public sealed partial class RestorePage : Page
{
    public RestoreViewModel ViewModel { get; }

    public RestorePage()
    {
        ViewModel = App.Services.GetRequiredService<RestoreViewModel>();
        this.InitializeComponent();
        this.DataContext = ViewModel;
        
        this.Loaded += async (s, e) =>
        {
            if (ViewModel.AvailableDisks.Count == 0)
                await ViewModel.LoadDisksCommand.ExecuteAsync(null);
        };
    }

    private void OnBrowseImageClick(object sender, RoutedEventArgs e)
    {
        if (NativeFileDialog.TryPickOpenFile(
                App.MainWindowHandle,
                "Disk Images (*.vhdx;*.vhd;*.img)\0*.vhdx;*.vhd;*.img\0All files (*.*)\0*.*\0\0",
                out var path))
        {
            ViewModel.SourceImagePath = path ?? string.Empty;
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
