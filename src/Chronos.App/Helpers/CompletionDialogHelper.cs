using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Chronos.App.Helpers;

/// <summary>
/// Shows a modal ContentDialog when an operation completes.
/// </summary>
internal static class CompletionDialogHelper
{
    /// <summary>
    /// Displays a single-button "OK" dialog with a title derived from the status message.
    /// </summary>
    public static async Task ShowCompletionDialogAsync(XamlRoot? xamlRoot, string statusMessage)
    {
        if (xamlRoot is null || string.IsNullOrEmpty(statusMessage))
            return;

        var dialog = new ContentDialog
        {
            Title = ClassifyTitle(statusMessage),
            Content = statusMessage,
            CloseButtonText = "OK",
            XamlRoot = xamlRoot
        };

        await dialog.ShowAsync();
    }

    private static string ClassifyTitle(string message)
    {
        if (message.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            return "Cancelled";
        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("error", StringComparison.OrdinalIgnoreCase))
            return "Error";
        return "Complete";
    }
}
