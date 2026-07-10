using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Reel.App.Mvvm;

/// <summary>
/// null → Visible, non-null → Collapsed (for "empty state" placeholders).
/// Pass ConverterParameter="invert" to flip it: non-null → Visible, null → Collapsed.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visibleWhenNull = !string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        var isNull = value is null;
        return isNull == visibleWhenNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
