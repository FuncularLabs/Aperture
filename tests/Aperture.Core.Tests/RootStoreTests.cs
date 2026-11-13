using Aperture.Core.Library;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>A folder's settings, kept with the library: whether its subfolders are taken in, and its alias.</summary>
public class RootStoreTests
{
    [Fact]
    public void SetRecursive_AndSetAlias_AreKept()
    {
        using var data = new TempDir();
        using var photos = new TempDir();
        long id;
        using (var library = new LibraryService(data.Path))
        {
            id = library.AddRoot(photos.Path, "Photos").Id;
            library.SetRecursive(id, false);
            library.SetAlias(id, "Holidays");
            library.ShutdownAsync().GetAwaiter().GetResult();
        }

        using var reopened = new LibraryService(data.Path);
        var root = Assert.Single(reopened.GetRoots());
        Assert.Equal((id, false, "Holidays"), (root.Id, root.Recursive, root.Alias));
        reopened.SetRecursive(id, true);
        Assert.True(Assert.Single(reopened.GetRoots()).Recursive);
        reopened.ShutdownAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void ForAFolderNoLongerThere_TheyChangeNothing()
    {
        using var data = new TempDir();
        using var photos = new TempDir();
        using var library = new LibraryService(data.Path);
        var root = library.AddRoot(photos.Path, "Photos");

        library.SetRecursive(root.Id + 100, false);
        library.SetAlias(root.Id + 100, "Other");

        var only = Assert.Single(library.GetRoots());
        Assert.Equal((true, "Photos"), (only.Recursive, only.Alias));
        library.ShutdownAsync().GetAwaiter().GetResult();
    }
}
