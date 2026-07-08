using System.Windows;

namespace Reel.App.Mvvm;

/// <summary>
/// Carries a DataContext into places the visual tree can't reach — notably
/// ContextMenu popups, where <c>RelativeSource AncestorType=Window</c> fails.
/// Put one in Window.Resources with <c>Data="{Binding}"</c> and bind through
/// <c>Data.SomeCommand</c> via <c>Source={StaticResource ...}</c>.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
