using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Reel.App.ViewModels;

namespace Reel.App.Mvvm;

/// <summary>
/// Maps <see cref="PreviewMode"/> to a layout value chosen by ConverterParameter, so the
/// browser grid and the preview pane can share a 2×2 grid and flip between right-dock and
/// bottom-dock without moving elements:
///   width/height → the preview's size on its axis (NaN off-axis);
///   vis → Visibility; prow/pcol/prowspan/pcolspan → the preview's cell;
///   browserRowSpan/browserColSpan → how far the browser grid spans.
/// </summary>
public sealed class PreviewModeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var mode = value is PreviewMode m ? m : PreviewMode.Off;
        var right = mode == PreviewMode.Right;
        var bottom = mode == PreviewMode.Bottom;
        return (parameter as string) switch
        {
            "width" => right ? 380.0 : double.NaN,
            "height" => bottom ? 340.0 : double.NaN,
            "vis" => mode == PreviewMode.Off ? Visibility.Collapsed : Visibility.Visible,
            "prow" => bottom ? 1 : 0,
            "pcol" => bottom ? 0 : 1,
            "prowspan" => bottom ? 1 : 2,
            "pcolspan" => bottom ? 2 : 1,
            "browserRowSpan" => right ? 2 : 1,     // right: browser spans both rows; else one
            "browserColSpan" => bottom ? 1 : 2,    // bottom: browser one column; else both

            // Inner preview layout: right = stacked (image top, tags, metadata);
            // bottom = three columns (tags | image | metadata).
            "row0h" => bottom ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            "row1h" => bottom ? new GridLength(0) : GridLength.Auto,
            "row2h" => right ? new GridLength(1, GridUnitType.Star) : new GridLength(0),
            "colLw" => bottom ? new GridLength(290) : new GridLength(0),
            "colRw" => bottom ? new GridLength(290) : new GridLength(0),
            "imgHeight" => right ? 250.0 : double.NaN,
            "tagsRow" => bottom ? 0 : 1,
            "tagsCol" => bottom ? 0 : 1,
            "metaRow" => bottom ? 0 : 2,
            "metaCol" => bottom ? 2 : 1,
            _ => Binding.DoNothing,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
