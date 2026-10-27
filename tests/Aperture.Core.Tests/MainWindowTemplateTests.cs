using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    /// The bindings in the objects <paramref name="template"/> builds and reaches, and in the grid's item-container
    /// style (<paramref name="containerStyle"/>, which every tile's container gets). On each element or content
    /// element: its bindings (a MultiBinding's or PriorityBinding's parts included); a Freezable or any other object it
    /// holds (a brush, an effect, a ToolTip object), and the items of a Freezable collection (gradient stops,
    /// transforms); its style; a template it sets; and its resources. In a style: its setters (and a setter's value
    /// that is a style, a template or an object), its triggers, its resources, and the style it's based on. In a
    /// template: its own triggers and resources, and what it builds. In triggers: DataTrigger and MultiDataTrigger
    /// bindings, and every trigger's setters. In resources: styles, templates, objects, and merged dictionaries.
    /// <see cref="TheBindingWalk_FindsABindingInEveryPlaceItWalks"/> plants a binding in each of these places, so
    /// removing any part of the walk that finds bindings fails it; the <c>seen</c> checks only stop repeats and loops.
    /// It looks only in the places listed — not, for example, at styles that apply implicitly from outside the template
    /// (the grid's or window's resources), DynamicResource references, InputBindings, or an element's own triggers;
    /// the tile and its container use none of them.
    /// </summary>
    private static List<(string Where, Binding Binding)> BindingsIn(DataTemplate template, Style? containerStyle = null)
    {
        var found = new List<(string, Binding)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (containerStyle is not null)
            VisitStyle(containerStyle, "container");
        VisitTriggers(template.Triggers, "template<triggers>");
        VisitResources(template.Resources, "template<resources>");
        Visit((DependencyObject)template.LoadContent(), "template");
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
                else if (values.Current.Value is Style style)
                    VisitStyle(style, at);
                else if (values.Current.Value is FrameworkTemplate template)
                    VisitTemplate(template, at + "<template>");
                else if (values.Current.Value is DependencyObject other)
                    Visit(other, at);
            }
            if (node is FrameworkElement element)
                VisitResources(element.Resources, where + "<resources>");
            if (node is FrameworkContentElement content)
                VisitResources(content.Resources, where + "<resources>");
            if (node is System.Collections.IEnumerable items) // a Freezable collection: gradient stops, transforms…
                foreach (var item in items.OfType<DependencyObject>())
                    Visit(item, $"{where}[]");
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                Visit(child, $"{where}/{child.GetType().Name}");
        }

        void VisitResources(ResourceDictionary resources, string where)
        {
            if (!seen.Add(resources))
                return;
            foreach (var key in resources.Keys.Cast<object>().ToList())
            {
                switch (resources[key])
                {
                    case Style style: VisitStyle(style, where); break;
                    case FrameworkTemplate template: VisitTemplate(template, where); break;
                    case DependencyObject other: Visit(other, where); break;
                }
            }
            foreach (var merged in resources.MergedDictionaries)
                VisitResources(merged, where);
        }

        void VisitTemplate(FrameworkTemplate template, string where)
        {
            if (!seen.Add(template))
                return;
            VisitResources(template.Resources, where + "<resources>");
            if (template is ControlTemplate control)
                VisitTriggers(control.Triggers, where + "<triggers>");
            if (template is DataTemplate data)
                VisitTriggers(data.Triggers, where + "<triggers>");
            if (template.LoadContent() is DependencyObject content)
                Visit(content, where);
        }

        void VisitStyle(Style style, string where)
        {
            if (!seen.Add(style))
                return;
            VisitSetters(style.Setters, where);
            VisitTriggers(style.Triggers, where + "<triggers>");
            VisitResources(style.Resources, where + "<resources>");
            if (style.BasedOn is { } basedOn)
                VisitStyle(basedOn, where + "<based on>");
        }

        void VisitTriggers(TriggerCollection triggers, string where)
        {
            foreach (var trigger in triggers)
            {
                switch (trigger)
                {
                    case DataTrigger data:
                        if (data.Binding is { } binding)
                            Add(binding, where);
                        VisitSetters(data.Setters, where);
                        break;
                    case MultiDataTrigger multi:
                        foreach (var condition in multi.Conditions.Where(c => c.Binding is not null))
                            Add(condition.Binding, where);
                        VisitSetters(multi.Setters, where);
                        break;
                    case Trigger property:
                        VisitSetters(property.Setters, where);
                        break;
                    case MultiTrigger multiProperty:
                        VisitSetters(multiProperty.Setters, where);
                        break;
                }
            }
        }

        void VisitSetters(SetterBaseCollection setters, string where)
        {
            foreach (var setter in setters.OfType<Setter>())
            {
                var at = $"{where}<setter {setter.Property?.Name}>";
                switch (setter.Value)
                {
                    case BindingBase bound: Add(bound, at); break;
                    case Style style: VisitStyle(style, at); break;
                    case FrameworkTemplate template: VisitTemplate(template, at); break;
                    case DependencyObject other: Visit(other, at); break;
                }
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

    // The bindings a library switch would re-evaluate: through the window, its view model, or the view-model proxy.
    private static List<string> Offenders(IEnumerable<(string Where, Binding Binding)> bindings)
    {
        var offenders = new List<string>();
        foreach (var (where, binding) in bindings)
        {
            var what = $"{where} <- {binding.Path?.Path}";
            if (binding.RelativeSource?.AncestorType is { } ancestor && typeof(Window).IsAssignableFrom(ancestor))
                offenders.Add(what + " (through the window)");
            else if (binding.Source is BindingProxy)
                offenders.Add(what + " (through the view-model proxy)");
            else if (binding.Path?.Path.StartsWith("DataContext.", StringComparison.Ordinal) == true)
                offenders.Add(what + " (an ancestor's view model)");
        }
        return offenders;
    }

    [Fact]
    public void TheBindingWalk_FindsABindingInEveryPlaceItWalks()
    {
        // The walk's completeness, separate from today's tile: one window binding planted in each place the walk claims
        // to reach (see BindingsIn) — each part of the walk that finds bindings has one — and every one must be found.
        Sta.Run(() =>
        {
            const string Window = "RelativeSource={RelativeSource AncestorType=Window}";
            var template = (DataTemplate)System.Windows.Markup.XamlReader.Parse($$"""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <DataTemplate.Resources>
                    <Style TargetType="TextBlock">
                      <Setter Property="Tag" Value="{Binding DataContext.TemplateResources, {{Window}}}" />
                    </Style>
                  </DataTemplate.Resources>
                  <StackPanel x:Name="b" Tag="{Binding DataContext.Element, {{Window}}}">
                    <StackPanel.Resources>
                      <SolidColorBrush x:Key="r" Color="{Binding DataContext.ElementResources, {{Window}}}" />
                      <ControlTemplate x:Key="t" TargetType="Button">
                        <Border Tag="{Binding DataContext.ResourceTemplate, {{Window}}}" />
                      </ControlTemplate>
                    </StackPanel.Resources>
                    <StackPanel.Effect>
                      <DropShadowEffect BlurRadius="{Binding DataContext.Effect, {{Window}}}" />
                    </StackPanel.Effect>
                    <StackPanel.Background>
                      <ImageBrush ImageSource="{Binding DataContext.Brush, {{Window}}}" />
                    </StackPanel.Background>
                    <StackPanel.RenderTransform>
                      <TransformGroup>
                        <RotateTransform Angle="{Binding DataContext.TransformInACollection, {{Window}}}" />
                      </TransformGroup>
                    </StackPanel.RenderTransform>
                    <StackPanel.Style>
                      <Style TargetType="StackPanel">
                        <Style.BasedOn>
                          <Style TargetType="StackPanel">
                            <Setter Property="ToolTip" Value="{Binding DataContext.BasedOn, {{Window}}}" />
                          </Style>
                        </Style.BasedOn>
                        <Style.Resources>
                          <SolidColorBrush x:Key="s" Color="{Binding DataContext.StyleResources, {{Window}}}" />
                        </Style.Resources>
                        <Setter Property="Width" Value="{Binding DataContext.StyleSetter, {{Window}}}" />
                        <Setter Property="FocusVisualStyle">
                          <Setter.Value>
                            <Style>
                              <Setter Property="Control.Tag" Value="{Binding DataContext.SetterStyle, {{Window}}}" />
                            </Style>
                          </Setter.Value>
                        </Setter>
                        <Style.Triggers>
                          <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Height" Value="{Binding DataContext.TriggerSetter, {{Window}}}" />
                          </Trigger>
                          <MultiTrigger>
                            <MultiTrigger.Conditions>
                              <Condition Property="IsEnabled" Value="False" />
                            </MultiTrigger.Conditions>
                            <Setter Property="MinWidth" Value="{Binding DataContext.MultiTriggerSetter, {{Window}}}" />
                          </MultiTrigger>
                          <DataTrigger Binding="{Binding DataContext.StyleTrigger, {{Window}}}" Value="1">
                            <Setter Property="Opacity" Value="0.5" />
                          </DataTrigger>
                        </Style.Triggers>
                      </Style>
                    </StackPanel.Style>
                    <Border>
                      <Border.Resources>
                        <ResourceDictionary>
                          <ResourceDictionary.MergedDictionaries>
                            <ResourceDictionary>
                              <SolidColorBrush x:Key="m" Color="{Binding DataContext.MergedDictionary, {{Window}}}" />
                            </ResourceDictionary>
                          </ResourceDictionary.MergedDictionaries>
                        </ResourceDictionary>
                      </Border.Resources>
                      <Border.ToolTip>
                        <ToolTip Tag="{Binding DataContext.NonFreezableObject, {{Window}}}" />
                      </Border.ToolTip>
                      <Border.BorderBrush>
                        <LinearGradientBrush>
                          <GradientStop Color="{Binding DataContext.GradientStop, {{Window}}}" Offset="0" />
                        </LinearGradientBrush>
                      </Border.BorderBrush>
                    </Border>
                    <TextBlock>
                      <Run Text="x">
                        <Run.Resources>
                          <SolidColorBrush x:Key="c" Color="{Binding DataContext.ContentElementResources, {{Window}}}" />
                        </Run.Resources>
                      </Run>
                      <TextBlock.Tag>
                        <PriorityBinding>
                          <Binding Path="DataContext.Priority" RelativeSource="{RelativeSource AncestorType=Window}" />
                        </PriorityBinding>
                      </TextBlock.Tag>
                    </TextBlock>
                    <ContentControl>
                      <ContentControl.ContentTemplate>
                        <DataTemplate>
                          <Border />
                          <DataTemplate.Triggers>
                            <DataTrigger Binding="{Binding DataContext.SetTemplateTrigger, {{Window}}}" Value="1">
                              <Setter Property="Opacity" Value="0.5" />
                            </DataTrigger>
                          </DataTemplate.Triggers>
                        </DataTemplate>
                      </ContentControl.ContentTemplate>
                      <ContentControl.Style>
                        <Style TargetType="ContentControl">
                          <Setter Property="Template">
                            <Setter.Value>
                              <ControlTemplate TargetType="ContentControl">
                                <Border Tag="{Binding DataContext.SetterTemplate, {{Window}}}" />
                              </ControlTemplate>
                            </Setter.Value>
                          </Setter>
                          <Setter Property="Background">
                            <Setter.Value>
                              <SolidColorBrush Color="{Binding DataContext.SetterObject, {{Window}}}" />
                            </Setter.Value>
                          </Setter>
                        </Style>
                      </ContentControl.Style>
                    </ContentControl>
                    <Button>
                      <Button.Template>
                        <ControlTemplate TargetType="Button">
                          <ControlTemplate.Resources>
                            <SolidColorBrush x:Key="tr" Color="{Binding DataContext.SetTemplateResources, {{Window}}}" />
                          </ControlTemplate.Resources>
                          <TextBlock>
                            <TextBlock.Text>
                              <MultiBinding StringFormat="{}{0}">
                                <Binding Path="DataContext.Multi" RelativeSource="{RelativeSource AncestorType=Window}" />
                              </MultiBinding>
                            </TextBlock.Text>
                          </TextBlock>
                          <ControlTemplate.Triggers>
                            <DataTrigger Binding="{Binding DataContext.ControlTrigger, {{Window}}}" Value="1">
                              <Setter Property="Opacity" Value="0.5" />
                            </DataTrigger>
                          </ControlTemplate.Triggers>
                        </ControlTemplate>
                      </Button.Template>
                    </Button>
                  </StackPanel>
                  <DataTemplate.Triggers>
                    <DataTrigger Binding="{Binding DataContext.TemplateTrigger, {{Window}}}" Value="1">
                      <Setter TargetName="b" Property="Tag" Value="{Binding DataContext.TemplateTriggerSetter, {{Window}}}" />
                    </DataTrigger>
                    <MultiDataTrigger>
                      <MultiDataTrigger.Conditions>
                        <Condition Binding="{Binding DataContext.Condition, {{Window}}}" Value="1" />
                      </MultiDataTrigger.Conditions>
                      <Setter TargetName="b" Property="Opacity" Value="{Binding DataContext.MultiDataTriggerSetter, {{Window}}}" />
                    </MultiDataTrigger>
                  </DataTemplate.Triggers>
                </DataTemplate>
                """);

            var container = (Style)System.Windows.Markup.XamlReader.Parse($$"""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListBoxItem">
                  <Setter Property="Tag" Value="{Binding DataContext.ContainerStyle, {{Window}}}" />
                </Style>
                """);

            var found = Offenders(BindingsIn(template, container));

            string[] places =
            [
                "Element", "Brush", "TransformInACollection", "GradientStop", "ElementResources", "TemplateResources",
                "BasedOn", "StyleResources", "StyleSetter", "SetterStyle", "SetterTemplate", "SetterObject",
                "TriggerSetter", "MultiTriggerSetter", "StyleTrigger", "Priority", "Multi", "ControlTrigger",
                "TemplateTrigger", "TemplateTriggerSetter", "Condition", "MultiDataTriggerSetter", "Effect",
                "ResourceTemplate", "MergedDictionary", "ContentElementResources", "NonFreezableObject",
                "SetTemplateTrigger", "SetTemplateResources", "ContainerStyle",
            ];
            Assert.All(places, place =>
                Assert.Contains(found, offender => offender.Contains($"<- DataContext.{place} ", StringComparison.Ordinal)));
            Assert.Equal(places.Length, found.Count);
        });
    }

    [Fact]
    public void TheTileTemplatesXaml_WritesNoBindingThroughTheWindowOrTheProxy()
    {
        // Whatever the walk reaches, a binding written in the tile's own XAML — a resource, a trigger, a gradient stop, an
        // input binding, a place the walk doesn't visit — is in this text. It looks for the usual spellings (an
        // AncestorType of Window, the Vm proxy as a StaticResource or DynamicResource, a DataContext. path); the walk
        // checks the objects themselves.
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Aperture.App", "MainWindow.xaml"));
        var tile = TileTemplateText(xaml);

        Assert.Contains("{Binding Thumbnail}", tile); // it's the tile
        var offenders = new[]
            {
                @"AncestorType=\s*(\{x:Type\s+)?([\w.]+:)?(Main)?Window\b", // through the window
                @"\{(Static|Dynamic)Resource\s+Vm\}",                        // through the view-model proxy
                @"\bDataContext\.\w",                                        // an ancestor's view model
            }
            .SelectMany(pattern => System.Text.RegularExpressions.Regex.Matches(tile, pattern).Select(m => m.Value))
            .ToList();
        Assert.Empty(offenders);
    }

    // The text of <DataTemplate DataType="{x:Type vm:TileVm}"> … its matching </DataTemplate>, nested ones included.
    private static string TileTemplateText(string xaml)
    {
        const string Open = "<DataTemplate DataType=\"{x:Type vm:TileVm}\">";
        var start = xaml.IndexOf(Open, StringComparison.Ordinal);
        Assert.True(start >= 0, "the tile template wasn't found in MainWindow.xaml");
        var depth = 0;
        var tags = new System.Text.RegularExpressions.Regex(@"<(/?)DataTemplate\b[^>]*?(/?)>");
        foreach (System.Text.RegularExpressions.Match tag in tags.Matches(xaml, start))
        {
            if (tag.Groups[2].Value == "/")
                continue; // <DataTemplate … />
            depth += tag.Groups[1].Value == "/" ? -1 : 1;
            if (depth == 0)
                return xaml[start..(tag.Index + tag.Length)];
        }
        throw new InvalidOperationException("the tile template isn't closed");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "Aperture.App", "MainWindow.xaml")))
                return dir.FullName;
        throw new InvalidOperationException("the repository wasn't found above " + AppContext.BaseDirectory);
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

            Assert.Same(window.FindResource("TileContainer"), grid.ItemContainerStyle); // the style every tile's container gets
            var bindings = BindingsIn(template, grid.ItemContainerStyle);
            var offenders = Offenders(bindings);
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
    public void TheGrid_ShowsAnIndexingCardWhileIndexing()
    {
        Sta.Run(() =>
        {
            var window = new MainWindow();

            var card = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("IndexingCard"));
            Assert.Equal("IsIndexing", BindingOperations.GetBinding(card, UIElement.VisibilityProperty)?.Path.Path);
            Assert.Equal(VerticalAlignment.Bottom, card.VerticalAlignment); // a card over the grid, not a sheet across it
            var parts = LogicalDescendants(card).ToList();
            Assert.Contains(parts.OfType<TextBlock>(), t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "IndexingText");
            var bar = Assert.Single(parts.OfType<ProgressBar>(), p => BindingOperations.GetBinding(p, RangeBase.ValueProperty) is not null);
            Assert.Equal("IndexValue", BindingOperations.GetBinding(bar, RangeBase.ValueProperty)!.Path.Path);
            Assert.Equal("IndexMax", BindingOperations.GetBinding(bar, RangeBase.MaximumProperty)?.Path.Path);
            Assert.Equal("IndexIndeterminate", BindingOperations.GetBinding(bar, ProgressBar.IsIndeterminateProperty)?.Path.Path);
            window.Close();
        });
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in LogicalDescendants(child))
                yield return deeper;
        }
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
