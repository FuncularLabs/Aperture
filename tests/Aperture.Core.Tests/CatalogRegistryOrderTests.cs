using System.Text.Json;
using Aperture.Core.Storage;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The order the user dragged the libraries into, kept in the library list (0.9.1 batch, item 5). A list without one
/// (every list before 0.9.1) is shown alphabetically.
/// </summary>
public class CatalogRegistryOrderTests
{
    private static string RegistryPath(TempDir dir) => dir.Combine("catalogs.json");

    [Fact]
    public void ANewList_HasNoOrder()
    {
        using var dir = new TempDir();

        Assert.Null(new CatalogRegistry(RegistryPath(dir)).Order);
    }

    [Fact]
    public void SetOrder_IsKept_AcrossAReload()
    {
        using var dir = new TempDir();
        var registry = new CatalogRegistry(RegistryPath(dir));
        var a = registry.Add("A", dir.Combine("a"));
        var b = registry.Add("B", dir.Combine("b"));
        var c = registry.Add("C", dir.Combine("c"));

        Assert.True(registry.SetOrder([c.Id, a.Id, b.Id]));

        Assert.Equal([c.Id, a.Id, b.Id], registry.Order);
        Assert.Equal([c.Id, a.Id, b.Id], new CatalogRegistry(RegistryPath(dir)).Order);
    }

    [Fact]
    public void SetOrder_KeepsOnlyListedLibraries_EachOnce()
    {
        using var dir = new TempDir();
        var registry = new CatalogRegistry(RegistryPath(dir));
        var a = registry.Add("A", dir.Combine("a"));
        var b = registry.Add("B", dir.Combine("b"));

        Assert.True(registry.SetOrder(["nope", b.Id, a.Id, b.Id]));

        Assert.Equal([b.Id, a.Id], new CatalogRegistry(RegistryPath(dir)).Order);
    }

    [Fact]
    public void TheOrder_SurvivesAnotherWindowsChange()
    {
        using var dir = new TempDir();
        var mine = new CatalogRegistry(RegistryPath(dir));
        var a = mine.Add("A", dir.Combine("a"));
        var b = mine.Add("B", dir.Combine("b"));
        mine.SetOrder([b.Id, a.Id]);

        var theirs = new CatalogRegistry(RegistryPath(dir));
        Assert.True(theirs.Rename(a.Id, "Alpha")); // re-reads the list, then saves it

        Assert.Equal([b.Id, a.Id], new CatalogRegistry(RegistryPath(dir)).Order);
        mine.Reload();
        Assert.Equal([b.Id, a.Id], mine.Order);
    }

    [Fact]
    public void AnotherWindowsNewOrder_IsReadBeforeTheNextChange()
    {
        using var dir = new TempDir();
        var mine = new CatalogRegistry(RegistryPath(dir));
        var a = mine.Add("A", dir.Combine("a"));
        var b = mine.Add("B", dir.Combine("b"));

        var theirs = new CatalogRegistry(RegistryPath(dir));
        theirs.SetOrder([b.Id, a.Id]);
        Assert.True(mine.Rename(a.Id, "Alpha")); // mine saves after reading theirs: it doesn't put its old order back

        Assert.Equal([b.Id, a.Id], new CatalogRegistry(RegistryPath(dir)).Order);
    }

    [Fact]
    public void SetOrder_WhenTheListCannotBeSaved_ReturnsFalse_AndKeepsTheLastOrder()
    {
        using var dir = new TempDir();
        var path = RegistryPath(dir);
        var registry = new CatalogRegistry(path);
        var a = registry.Add("A", dir.Combine("a"));
        var b = registry.Add("B", dir.Combine("b"));
        registry.SetOrder([a.Id, b.Id]);

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.False(registry.SetOrder([b.Id, a.Id]));
            Assert.Equal([a.Id, b.Id], registry.Order);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        Assert.Equal([a.Id, b.Id], new CatalogRegistry(path).Order);
    }

    [Fact]
    public void AListFileWithoutAnOrder_Loads_AndOneWithAnOrderIsRead()
    {
        using var dir = new TempDir();
        var work = JsonSerializer.Serialize(dir.Combine("work"));
        var home = JsonSerializer.Serialize(dir.Combine("home"));
        File.WriteAllText(RegistryPath(dir),
            "{\"catalogs\":[{\"id\":\"a\",\"name\":\"Work\",\"dir\":" + work + "},{\"id\":\"b\",\"name\":\"Home\",\"dir\":" + home + "}]}");
        Assert.Null(new CatalogRegistry(RegistryPath(dir)).Order);

        File.WriteAllText(RegistryPath(dir),
            "{\"catalogs\":[{\"id\":\"a\",\"name\":\"Work\",\"dir\":" + work + "},{\"id\":\"b\",\"name\":\"Home\",\"dir\":" + home
            + "}],\"order\":[\"b\",\"a\"]}");
        Assert.Equal(["b", "a"], new CatalogRegistry(RegistryPath(dir)).Order);
    }

    [Theory]
    [InlineData("\"x\"")]                        // a string, not a list
    [InlineData("[1, 2]")]                         // numbers
    [InlineData("[null, \"b\", \"\", \"b\", {}, \"a\"]")] // nulls, blanks, repeats, objects among the ids
    [InlineData("{}")]
    [InlineData("true")]
    public void AMalformedOrder_IsIgnored_AndTheListKept(string order)
    {
        using var dir = new TempDir();
        var work = JsonSerializer.Serialize(dir.Combine("work"));
        var home = JsonSerializer.Serialize(dir.Combine("home"));
        File.WriteAllText(RegistryPath(dir),
            "{\"catalogs\":[{\"id\":\"a\",\"name\":\"Work\",\"dir\":" + work + "},{\"id\":\"b\",\"name\":\"Home\",\"dir\":"
            + home + "}],\"order\":" + order + "}");

        var registry = new CatalogRegistry(RegistryPath(dir));

        Assert.Equal(["a", "b"], registry.List().Select(c => c.Id)); // every library still listed
        Assert.Null(registry.QuarantinedFile);                         // and the file not set aside
        Assert.Equal(order.Contains("\"b\"") ? ["b", "a"] : [], registry.Order ?? []); // only the ids in it are kept
    }

    [Fact]
    public void TheOrder_SurvivesEveryOtherChange()
    {
        using var dir = new TempDir();
        var registry = new CatalogRegistry(RegistryPath(dir));
        var a = registry.Add("A", dir.Combine("a"));
        var b = registry.Add("B", dir.Combine("b"));
        Assert.True(registry.SetOrder([b.Id, a.Id]));

        var c = registry.Add("C", dir.Combine("c"));
        Assert.True(registry.Rename(a.Id, "Alpha"));
        Assert.True(registry.SetItemCount(b.Id, 12));
        Assert.True(registry.SetActive(b.Id));
        Assert.True(registry.Remove(c.Id));
        registry.Reload();

        Assert.Equal([b.Id, a.Id], registry.Order);
        Assert.Equal([b.Id, a.Id], new CatalogRegistry(RegistryPath(dir)).Order);
    }
}
