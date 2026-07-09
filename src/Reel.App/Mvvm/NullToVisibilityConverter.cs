using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Reel.App.Mvvm;

/// <summary>null → Visible, non-null → Collapsed. For "empty state" placeholders.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
