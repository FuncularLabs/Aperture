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
/// may bind through the window or the view-model proxy, and the grid has one tile menu, not one per tile. Also the
/// title's parts and their order. The real <see cref="MainWindow"/>, loaded on an STA thread (never shown).
/// </summary>
public class MainWindowTemplateTests
{
    /// <summary>
    /// Every binding under <paramref name="root"/>, wherever WPF keeps one: on an element or a Freezable it holds (an
    /// image brush, an effect), inside a MultiBinding or PriorityBinding, in a Style's setters and triggers, and in a
    /// template the element sets (loaded and walked the same way).
    /// </summary>
    private static List<(string Where, Binding Binding)> BindingsUnder(DependencyObject root)
    {
        var found = new List<(string, Binding)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(root, root.GetType().Name);
        return found;

        void Visit(DependencyObject node, string where)
        {
            if (!seen.Add(node))
                return;
            var values = node.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                var property = values.Current.Property;
                var at = $"{where}.{property.Name}";
                if (BindingOperations.GetBindingBase(node, property) is { } bound)
                    Add(bound, at);
                else if (values.Current.Value is Freezable freezable)
                    Visit(freezable, at);
                else if (values.Current.Value is Style style)
                    VisitStyle(style, at);
                else if (values.Current.Value is FrameworkTemplate template && template.LoadContent() is DependencyObject content)
                    Visit(content, at + "<template>");
                else if (values.Current.Value is DependencyObject other)
                    Visit(other, at);
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                Visit(child, $"{where}/{child.GetType().Name}");
        }

        void VisitStyle(Style style, string where)
        {
            foreach (var setter in style.Setters.OfType<Setter>().Where(s => s.Value is BindingBase))
                Add((BindingBase)setter.Value, $"{where}<setter {setter.Property.Name}>");
            foreach (var trigger in style.Triggers)
            {
                if (trigger is DataTrigger data && data.Binding is { } dataBinding)
                    Add(dataBinding, $"{where}<trigger>");
                if (trigger is MultiDataTrigger multi)
                    foreach (var condition in multi.Conditions.Where(c => c.Binding is not null))
                        Add(condition.Binding, $"{where}<trigger>");
            }
        }

        void Add(BindingBase bound, string where)
        {
            switch (bound)
            {
                case Binding binding: found.Add((where, binding)); break;
                case MultiBinding multi: foreach (var part in multi.Bindings) Add(part, where + "<multi>"); break;
                case PriorityBinding priority: foreach (var part in priority.Bindings) Add(part, where + "<priority>"); break;
            }
        }
    }

    private static bool HasOwnMenu(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().Any(HasOwnMenu)
        || root.ReadLocalValue(FrameworkElement.ContextMenuProperty) != DependencyProperty.UnsetValue;

    [Fact]
    public void TheTileTemplate_BindsNothingThroughTheWindowOrTheProxy()
    {
        Sta.Run(() =>
        {
            var window = new MainWindow();
            var grid = (ListBox)window.FindName("ItemsList");
            var template = (DataTemplate)grid.Resources[new DataTemplateKey(typeof(TileVm))];
            var tile = (DependencyObject)template.LoadContent();

            var bindings = BindingsUnder(tile);
            var offenders = new List<string>();
            foreach (var (where, binding) in bindings)
            {
                var what = $"{where} <- {binding.Path?.Path}";
                if (binding.RelativeSource?.AncestorType is { } ancestor && typeof(Window).IsAssignableFrom(ancestor))
                    offenders.Add(what + " (through the window)");
                if (binding.Source is BindingProxy)
                    offenders.Add(what + " (through the view-model proxy)");
                if (binding.Path?.Path.StartsWith("DataContext.", StringComparison.Ordinal) == true)
                    offenders.Add(what + " (an ancestor's view model)");
            }
            var sizeBindings = bindings.Count(b => b.Binding.RelativeSource?.Mode == RelativeSourceMode.Self
                && (b.Binding.Path?.Path.Contains("TileLayout.Size", StringComparison.Ordinal) == true
                    || b.Binding.Path?.PathParameters.Contains(TileLayout.SizeProperty) == true));

            Assert.Empty(offenders);
            Assert.False(HasOwnMenu(tile), "a tile has a menu of its own");
            Assert.Equal(4, sizeBindings); // the tile, its picture's width and height, its caption: the inherited size
            Assert.Contains(bindings, b => b.Binding.Path?.Path == "Thumbnail" && b.Where.Contains("Background")); // the walk reaches the image brush
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

    [Fact]
    public void TheTitle_IsTheProductThenTheLibraryThenTheFolder()
    {
        Sta.Run(() =>
        {
            var window = new MainWindow();

            var title = BindingOperations.GetMultiBinding(window, Window.TitleProperty);

            Assert.NotNull(title);
            Assert.IsType<WindowTitleConverter>(title!.Converter);
            Assert.Equal(["AppVersion", "Catalog.ActiveName", "LocationTitle"],
                title.Bindings.OfType<Binding>().Select(b => b.Path.Path));
            window.Close();
        });
    }
}
