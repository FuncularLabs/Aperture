using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Aperture.App;
using Aperture.App.Mvvm;
using Aperture.App.ViewModels;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Switching libraries stays quick (Paul's trial, 2026-10-26): a library switch gives the window a new view
/// model, and WPF re-evaluates every binding that goes through it — on tiles already discarded, too. So no tile
/// may bind through the window or the view-model proxy, and the grid has one tile menu, not one per tile. The
/// real <see cref="MainWindow"/>, loaded on an STA thread (never shown).
/// </summary>
public class MainWindowTemplateTests
{
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }

    [Fact]
    public void TheTileTemplate_BindsNothingThroughTheWindowOrTheProxy()
    {
        Sta.Run(() =>
        {
            var window = new MainWindow();
            var grid = (ListBox)window.FindName("ItemsList");
            var template = (DataTemplate)grid.Resources[new DataTemplateKey(typeof(TileVm))];
            var tile = (DependencyObject)template.LoadContent();

            var offenders = new List<string>();
            var sizeBindings = 0;
            foreach (var element in Descendants(tile))
            {
                if (element.ReadLocalValue(FrameworkElement.ContextMenuProperty) != DependencyProperty.UnsetValue)
                    offenders.Add($"{element.GetType().Name} has a menu of its own");
                var values = element.GetLocalValueEnumerator();
                while (values.MoveNext())
                {
                    if (BindingOperations.GetBinding(element, values.Current.Property) is not { } binding)
                        continue;
                    var where = $"{element.GetType().Name}.{values.Current.Property.Name} <- {binding.Path?.Path}";
                    if (binding.RelativeSource?.AncestorType is { } ancestor && typeof(Window).IsAssignableFrom(ancestor))
                        offenders.Add(where + " (through the window)");
                    if (binding.Source is BindingProxy)
                        offenders.Add(where + " (through the view-model proxy)");
                    if (binding.Path?.Path.StartsWith("DataContext.", StringComparison.Ordinal) == true)
                        offenders.Add(where + " (an ancestor's view model)");
                    if (binding.RelativeSource?.Mode == RelativeSourceMode.Self
                        && (binding.Path?.Path.Contains("TileLayout.Size", StringComparison.Ordinal) == true
                            || binding.Path?.PathParameters.Contains(TileLayout.SizeProperty) == true))
                        sizeBindings++;
                }
            }

            Assert.Empty(offenders);
            Assert.Equal(4, sizeBindings); // the tile, its picture's width and height, its caption: the inherited size
            window.Close();
        });
    }

    [Fact]
    public void TheGridHasOneTileMenu_ActingOnItsDataContext()
    {
        Sta.Run(() =>
        {
            var window = new MainWindow();
            var grid = (ListBox)window.FindName("ItemsList");

            var menu = Assert.IsType<ContextMenu>(grid.ContextMenu);
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(
                ["Preview", "Open", "Open with", "Open containing folder", "Tags & notes…", "Copy image", "Copy thumbnail",
                 "Copy file name", "Copy full path", "Copy", "Cut"],
                items.Select(i => (string)i.Header));
            var withTile = items.Select(i => BindingOperations.GetBinding(i, MenuItem.CommandParameterProperty)).OfType<Binding>().ToList();
            Assert.Equal(8, withTile.Count);
            Assert.All(withTile, b => Assert.True(b.Path is null || b.Path.Path.Length == 0)); // {Binding}: the tile it opened on
            Assert.Equal("TileSize", BindingOperations.GetBinding(grid, TileLayout.SizeProperty)!.Path.Path); // set once, here
            window.Close();
        });
    }
}
