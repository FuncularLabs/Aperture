using System.Windows;

namespace Aperture.App.Mvvm;

/// <summary>
/// The grid's tile size, set once on the grid and inherited by every tile, so a tile binds to its own copy
/// (<c>RelativeSource Self</c>) instead of reaching up to the window's view model. That matters on a library
/// switch: a binding through the window's DataContext is re-evaluated whenever the window gets a new view model,
/// and so is every such binding on tiles WPF has discarded but not yet collected — thousands of them, which made
/// each switch slower than the last. An inherited value reaches only the tiles still in the grid.
/// </summary>
public static class TileLayout
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(double), typeof(TileLayout),
        new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetSize(DependencyObject element) => (double)element.GetValue(SizeProperty);

    public static void SetSize(DependencyObject element, double value) => element.SetValue(SizeProperty, value);
}
