using Microsoft.UI.Xaml.Data;

namespace Chronos.App.Converters;

/// <summary>
/// Converter that negates a boolean value.
/// </summary>
public sealed class BoolNegationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool b ? !b : true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value is bool b ? !b : false;
    }
}
