using System.Globalization;
using System.Windows.Data;

namespace Aperture.App.Mvvm;

/// <summary>
/// The window title: product and version, then the library's name, then the folder being shown —
/// "Aperture Image Browser v0.9.0  —  Main  —  D:\Photos\Trip". Parts that are missing are left out: the folder at
/// the top of a library, the library's name when <c>APERTURE_DATA_DIR</c> pins one (no library list to name it).
/// </summary>
public sealed class WindowTitleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        string.Join("  —  ", values.OfType<string>().Where(part => part.Length > 0));

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
