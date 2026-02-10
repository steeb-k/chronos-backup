using Chronos.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Chronos.App.Views;

public sealed partial class BrowsePage : Page
{
    public BrowseViewModel ViewModel { get; }

    public BrowsePage()
    {
        ViewModel = App.Services.GetRequiredService<BrowseViewModel>();
        this.InitializeComponent();
        this.DataContext = ViewModel;
    }
}
