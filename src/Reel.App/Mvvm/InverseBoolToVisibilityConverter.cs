using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Reel.App.Mvvm;

/// <summary>True → Collapsed, False → Visible. The complement of the built-in converter.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
