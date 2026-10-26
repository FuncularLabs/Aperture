using System.Globalization;
using System.Windows;
using Aperture.App.Mvvm;

namespace Aperture.Core.Tests;

/// <summary>The title bar names the library before the folder (Paul, 2026-10-26).</summary>
public class WindowTitleConverterTests
{
    private static readonly WindowTitleConverter Converter = new();

    private static object Title(params object?[] values) =>
        Converter.Convert(values!, typeof(string), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void ProductThenLibraryThenFolder()
    {
        Assert.Equal(@"Aperture Image Browser v1.2.3  —  Main  —  D:\Photos\Trip",
            Title("Aperture Image Browser v1.2.3", "Main", @"D:\Photos\Trip"));
    }

    [Fact]
    public void AtTheTopOfALibrary_ProductThenLibrary()
    {
        Assert.Equal("Aperture Image Browser v1.2.3  —  Main", Title("Aperture Image Browser v1.2.3", "Main", ""));
    }

    [Fact]
    public void WithAPinnedLibrary_ThereIsNoLibraryName()
    {
        // APERTURE_DATA_DIR: no library list, so the Catalog.ActiveName binding has nothing to give.
        Assert.Equal(@"Aperture Image Browser v1.2.3  —  D:\Photos",
            Title("Aperture Image Browser v1.2.3", DependencyProperty.UnsetValue, @"D:\Photos"));
        Assert.Equal("Aperture Image Browser v1.2.3", Title("Aperture Image Browser v1.2.3", null, null));
    }

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            Converter.ConvertBack("x", [typeof(string)], null!, CultureInfo.InvariantCulture));
    }
}
