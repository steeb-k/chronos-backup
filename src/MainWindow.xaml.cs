using Chronos.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Chronos.App;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigationService;

    public MainWindow()
    {
        this.InitializeComponent();

        // Remove system title bar, keep border for resizing; use custom window controls
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null)
            presenter.SetBorderAndTitleBar(true, false);

        this.ExtendsContentIntoTitleBar = true;
        // Don't use SetTitleBar - it would consume all input. Use SetDragRectangles to limit drag to nav pane only.

        // Enable Mica backdrop effect
        if (MicaController.IsSupported())
        {
            this.SystemBackdrop = new MicaBackdrop() 
            { 
                Kind = MicaKind.Base 
            };
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            this.SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        // Configure navigation service with the content frame
        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _navigationService.Initialize(ContentFrame);
    }

    private const int NavPaneWidth = 220;
    private const int ContentTopDragHeight = 48;

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLogoForTheme();
        NavView.ActualThemeChanged += (_, _) => UpdateLogoForTheme();

        // Select the first item (Backup) on load
        NavView.SelectedItem = NavView.MenuItems[0];

        UpdateDragRegion();
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDragRegion();
    }

    private void UpdateDragRegion()
    {
        if (AppWindow?.TitleBar is not { } titleBar || !AppWindowTitleBar.IsCustomizationSupported())
            return;

        var scale = (float)RootGrid.XamlRoot.RasterizationScale;
        var totalW = (int)(RootGrid.ActualWidth * scale);
        var totalH = Math.Max(1, (int)(RootGrid.ActualHeight * scale));
        var navW = (int)(NavPaneWidth * scale);
        var topH = (int)(ContentTopDragHeight * scale);
        var contentW = Math.Max(0, totalW - navW);

        var rects = new Windows.Graphics.RectInt32[]
        {
            new(0, 0, navW, totalH),
            new(navW, 0, contentW, topH)
        };
        titleBar.SetDragRectangles(rects);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            _navigationService.NavigateTo(tag ?? "backup");
        }
    }

    private void UpdateLogoForTheme()
    {
        var isDark = NavView.ActualTheme == ElementTheme.Dark;
        LogoDark.Visibility = isDark ? Visibility.Visible : Visibility.Collapsed;
        LogoLight.Visibility = isDark ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter op)
            op.Minimize();
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            if (op.State == OverlappedPresenterState.Maximized)
            {
                op.Restore();
                MaximizeIcon.Glyph = "\uE922"; // Maximize
            }
            else
            {
                op.Maximize();
                MaximizeIcon.Glyph = "\uE923"; // Restore
            }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void OnContentTopDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            if (op.State == OverlappedPresenterState.Maximized)
            {
                op.Restore();
                MaximizeIcon.Glyph = "\uE922";
            }
            else
            {
                op.Maximize();
                MaximizeIcon.Glyph = "\uE923";
            }
        }
    }
}
