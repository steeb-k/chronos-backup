using Chronos.App.Services;
using Chronos.App.ViewModels;
using Chronos.App.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;

namespace Chronos.App;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigationService;
    private readonly IBackupOperationsService _backupOperationsService;
    private PropertyChangedEventHandler? _restorePropertyHandler;

    public MainWindow()
    {
        this.InitializeComponent();

        // Logo is now a pure XAML Path-based UserControl (ChronosLogo) that renders
        // via Direct2D — no WIC/bitmap decoding needed. Works in both desktop and WinPE.

        // Remove system title bar, keep border for resizing; use custom window controls
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null)
            presenter.SetBorderAndTitleBar(true, false);

        this.ExtendsContentIntoTitleBar = true;
        // Don't use SetTitleBar - it would consume all input. Use SetDragRectangles to limit drag to nav pane only.

        // Enable Mica backdrop effect (skip in WinPE - compositor interfaces incomplete)
        if (!Chronos.Common.Helpers.PeEnvironment.IsWinPE)
        {
            try
            {
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
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Backdrop not supported in this environment - continue without it
            }
        }

        // Configure navigation service with the content frame
        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _backupOperationsService = App.Services.GetRequiredService<IBackupOperationsService>();
        _navigationService.Initialize(ContentFrame);

        // Watch backup/clone operation state to disable nav items
        if (_backupOperationsService is ObservableObject backupObservable)
        {
            backupObservable.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IBackupOperationsService.IsBackupInProgress))
                    UpdateNavItemsEnabled();
            };
        }

        // Watch for RestorePage navigations to subscribe to its ViewModel
        ContentFrame.Navigated += OnContentFrameNavigated;
    }

    private const int NavPaneWidth = 220;
    private const int ContentTopDragHeight = 48;

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Program.Log("  NavView_Loaded: updating logo...");
            UpdateLogoForTheme();
            NavView.ActualThemeChanged += (_, _) =>
            {
                try { UpdateLogoForTheme(); }
                catch (Exception ex) { Program.Log("  ThemeChanged logo FAILED: " + ex.Message); Program.FlushLog(); }
            };
            Program.Log("  NavView_Loaded: selecting first item...");
            NavView.SelectedItem = NavView.MenuItems[0];
            Program.Log("  NavView_Loaded: updating drag region...");
            UpdateDragRegion();
            Program.Log("  NavView_Loaded: complete");
            Program.FlushLog();
        }
        catch (Exception ex)
        {
            Program.Log("  NavView_Loaded FAILED: " + ex.GetType().Name + ": " + ex.Message);
            Program.Log("  Stack: " + ex.StackTrace);
            Program.FlushLog();
        }
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try { UpdateDragRegion(); }
        catch (Exception ex)
        {
            Program.Log("  OnRootGridSizeChanged FAILED: " + ex.Message);
            Program.FlushLog();
        }
    }

    private void UpdateDragRegion()
    {
        if (AppWindow?.TitleBar is not { } titleBar || !AppWindowTitleBar.IsCustomizationSupported())
            return;

        if (RootGrid.XamlRoot is null)
            return; // Not yet connected to visual tree

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

    private void OnContentFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // Unsubscribe from any previous RestorePage's ViewModel
        if (_restorePropertyHandler is not null)
        {
            _restorePropertyHandler = null;
        }

        // Subscribe to RestorePage's ViewModel when navigated to it
        if (e.Content is RestorePage restorePage)
        {
            _restorePropertyHandler = (_, pe) =>
            {
                if (pe.PropertyName == nameof(RestoreViewModel.IsRestoreInProgress))
                    UpdateNavItemsEnabled();
            };
            restorePage.ViewModel.PropertyChanged += _restorePropertyHandler;
        }
    }

    /// <summary>
    /// Enables or disables all non-selected navigation items based on whether an operation is running.
    /// </summary>
    private void UpdateNavItemsEnabled()
    {
        bool locked = _backupOperationsService.IsBackupInProgress
            || (ContentFrame.Content is RestorePage rp && rp.ViewModel.IsRestoreInProgress);

        foreach (var obj in NavView.MenuItems)
        {
            if (obj is NavigationViewItem navItem && !ReferenceEquals(navItem, NavView.SelectedItem))
                navItem.IsEnabled = !locked;
        }
        foreach (var obj in NavView.FooterMenuItems)
        {
            if (obj is NavigationViewItem navItem && !ReferenceEquals(navItem, NavView.SelectedItem))
                navItem.IsEnabled = !locked;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        try
        {
            if (args.SelectedItem is NavigationViewItem item)
            {
                var tag = item.Tag?.ToString();
                Program.Log("  NavView_SelectionChanged: navigating to '" + (tag ?? "backup") + "'");
                Program.FlushLog();
                _navigationService.NavigateTo(tag ?? "backup");
                Program.Log("  NavView_SelectionChanged: navigation complete");
                Program.FlushLog();
            }
        }
        catch (Exception ex)
        {
            Program.Log("  NavView_SelectionChanged FAILED: " + ex.GetType().Name + ": " + ex.Message);
            Program.Log("  Stack: " + ex.StackTrace);
            Program.FlushLog();
        }
    }

    private void UpdateLogoForTheme()
    {
        // Logo is now a single ChronosLogo control using {ThemeResource TextFillColorPrimaryBrush}.
        // It auto-adapts to light/dark theme — no manual toggling needed.
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
