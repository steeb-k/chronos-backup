using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Chronos.App.Converters;

/// <summary>
/// Converter that converts a non-empty string to Visible, empty/null to Collapsed.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is string s && !string.IsNullOrEmpty(s) 
            ? Visibility.Visible 
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
