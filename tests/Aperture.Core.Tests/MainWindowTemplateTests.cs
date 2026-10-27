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
    /// Every binding in <paramref name="template"/>, wherever WPF keeps one: on an element or a Freezable it holds (an
    /// image brush, an effect), inside a MultiBinding or PriorityBinding, in a Style (its setters, its triggers'
    /// conditions and setters, and the style it's based on), in the template's own triggers, and in a template an
    /// element sets (loaded and walked the same way, triggers included).
    /// </summary>
    private static List<(string Where, Binding Binding)> BindingsIn(DataTemplate template)
    {
        var found = new List<(string, Binding)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        VisitTriggers(template.Triggers, "template<triggers>");
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
                else if (values.Current.Value is Freezable freezable)
                    Visit(freezable, at);
                else if (values.Current.Value is Style style)
                    VisitStyle(style, at);
                else if (values.Current.Value is FrameworkTemplate template)
                    VisitTemplate(template, at + "<template>");
                else if (values.Current.Value is DependencyObject other)
                    Visit(other, at);
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                Visit(child, $"{where}/{child.GetType().Name}");
        }

        void VisitTemplate(FrameworkTemplate template, string where)
        {
            if (!seen.Add(template))
                return;
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
    public void TheBindingWalk_FindsABindingWhereverATemplateCanHoldOne()
    {
        // The walk's completeness, separate from today's tile: one window binding in each place a template can keep one.
        Sta.Run(() =>
        {
            const string Window = "RelativeSource={RelativeSource AncestorType=Window}";
            var template = (DataTemplate)System.Windows.Markup.XamlReader.Parse($$"""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                  <Border x:Name="b" Tag="{Binding DataContext.Element, {{Window}}}">
                    <Border.Background>
                      <ImageBrush ImageSource="{Binding DataContext.Brush, {{Window}}}" />
                    </Border.Background>
                    <Border.Style>
                      <Style TargetType="Border">
                        <Style.BasedOn>
                          <Style TargetType="Border">
                            <Setter Property="ToolTip" Value="{Binding DataContext.BasedOn, {{Window}}}" />
                          </Style>
                        </Style.BasedOn>
                        <Setter Property="Width" Value="{Binding DataContext.StyleSetter, {{Window}}}" />
                        <Style.Triggers>
                          <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Height" Value="{Binding DataContext.TriggerSetter, {{Window}}}" />
                          </Trigger>
                          <DataTrigger Binding="{Binding DataContext.StyleTrigger, {{Window}}}" Value="1">
                            <Setter Property="Opacity" Value="0.5" />
                          </DataTrigger>
                        </Style.Triggers>
                      </Style>
                    </Border.Style>
                    <Button>
                      <Button.Template>
                        <ControlTemplate TargetType="Button">
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
                  </Border>
                  <DataTemplate.Triggers>
                    <DataTrigger Binding="{Binding DataContext.TemplateTrigger, {{Window}}}" Value="1">
                      <Setter TargetName="b" Property="Tag" Value="{Binding DataContext.TemplateTriggerSetter, {{Window}}}" />
                    </DataTrigger>
                    <MultiDataTrigger>
                      <MultiDataTrigger.Conditions>
                        <Condition Binding="{Binding DataContext.Condition, {{Window}}}" Value="1" />
                      </MultiDataTrigger.Conditions>
                      <Setter TargetName="b" Property="Opacity" Value="0.5" />
                    </MultiDataTrigger>
                  </DataTemplate.Triggers>
                </DataTemplate>
                """);

            var found = Offenders(BindingsIn(template));

            foreach (var place in new[] { "Element", "Brush", "BasedOn", "StyleSetter", "TriggerSetter", "StyleTrigger",
                                          "Multi", "ControlTrigger", "TemplateTrigger", "TemplateTriggerSetter", "Condition" })
                Assert.Contains(found, offender => offender.Contains($"<- DataContext.{place} ", StringComparison.Ordinal));
        });
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

            var bindings = BindingsIn(template);
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
