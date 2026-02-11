using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Chronos.App.Services;

public interface INavigationService
{
    void Initialize(Frame frame);
    void NavigateTo(string pageTag);
    void GoBack();
    bool CanGoBack { get; }
}

public class NavigationService : INavigationService
{
    private Frame? _frame;

    private static readonly Dictionary<string, Type> PageMap = new()
    {
        ["backup"] = typeof(Views.BackupPage),
        ["clone"] = typeof(Views.ClonePage),
        ["restore"] = typeof(Views.RestorePage),
        ["verify"] = typeof(Views.VerifyPage),
        ["browse"] = typeof(Views.BrowsePage),
        ["options"] = typeof(Views.OptionsPage),
    };

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public void Initialize(Frame frame)
    {
        _frame = frame;
    }

    public void NavigateTo(string pageTag)
    {
        if (_frame is null) return;

        if (PageMap.TryGetValue(pageTag, out var pageType))
        {
            // Don't navigate if we're already on this page
            if (_frame.CurrentSourcePageType != pageType)
            {
                _frame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
                // Clear back stack to prevent any layout state accumulation
                _frame.BackStack.Clear();
            }
        }
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack();
        }
    }
}
