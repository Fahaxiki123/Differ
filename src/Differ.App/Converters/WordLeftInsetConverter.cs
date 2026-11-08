using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Differ.App.Converters;

public sealed class WordLeftInsetConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        var inset = value is double doubleValue ? doubleValue : 0d;
        return new Thickness(Math.Max(0, inset), 0, 0, 0);
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
