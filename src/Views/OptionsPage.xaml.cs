using Chronos.App.ViewModels;
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
}
