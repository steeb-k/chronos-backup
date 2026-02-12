using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Chronos.App.Views;

public sealed partial class HistoryPage : Page
{
    public ViewModels.HistoryViewModel ViewModel { get; }

    public HistoryPage()
    {
        ViewModel = App.Services.GetRequiredService<ViewModels.HistoryViewModel>();
        this.InitializeComponent();
        this.DataContext = ViewModel;
    }
}
