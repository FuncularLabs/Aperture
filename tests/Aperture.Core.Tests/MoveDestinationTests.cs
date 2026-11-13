using Aperture.Core.Library;
using Aperture.Core.Models;

namespace Aperture.Core.Tests;

/// <summary>
/// Where a Move to… folder is (0.9.2 M2): one this library indexes takes the files without a question; any other is
/// in another library, under one of this library's folders but not indexed, or in none.
/// </summary>
public class MoveDestinationTests
{
    private static Root R(long id, string path, bool recursive = true, bool included = true) =>
        new() { Id = id, Path = path, Alias = Path.GetFileName(path), Recursive = recursive, Included = included };

    private static readonly Root[] Roots =
    [
        R(1, @"C:\Photos"),
        R(2, @"D:\Phone", recursive: false),
        R(3, @"C:\Photos\Trips\2024", recursive: false), // nested in Photos
        R(4, @"E:\Off", included: false),               // unticked: still this library's
    ];

    private static readonly OtherLibrary[] Others =
    [
        new("Work", [new LibraryFolder(@"F:\Work", Recursive: true), new LibraryFolder(@"D:\Phone\Shared", Recursive: true)]),
        new("Archive", [new LibraryFolder(@"G:\Old", Recursive: false)]),
    ];

    [Theory]
    [InlineData(@"C:\Photos", 1L, "")]
    [InlineData(@"C:\Photos\", 1L, "")]                    // a trailing separator
    [InlineData(@"c:\photos\Trips", 1L, "Trips")]          // any case
    [InlineData(@"C:\Photos\Trips\2024", 3L, "")]          // the deepest folder that indexes it
    [InlineData(@"C:\Photos\Trips\2024\Rome", 1L, @"Trips\2024\Rome")] // not by 2024 (no subfolders), by Photos
    [InlineData(@"D:\Phone", 2L, "")]                      // a folder without subfolders: itself
    [InlineData(@"C:\Photos\Hidden", 1L, "Hidden")]        // a hidden subfolder is still indexed
    [InlineData(@"E:\Off\Sub", 4L, "Sub")]                 // so is an unticked folder
    public void AFolderThisLibraryIndexes(string folder, long rootId, string relDir)
    {
        var at = MoveDestination.Classify(folder, Roots, Others);

        Assert.Equal(MoveDestinationKind.ThisLibrary, at.Kind);
        Assert.Equal((rootId, relDir), (at.RootId, at.RelDir));
        Assert.Equal(relDir.Length == 0 ? Roots.Single(r => r.Id == rootId).Path : Path.Combine(Roots.Single(r => r.Id == rootId).Path, relDir),
            at.FolderPath); // built from the folder's own path, as a row's full path is
    }

    [Fact]
    public void ASubfolderOfAFolderWithoutSubfolders_IsntIndexed()
    {
        var at = MoveDestination.Classify(@"D:\Phone\Camera", Roots, Others);

        Assert.Equal(MoveDestinationKind.NotIndexedHere, at.Kind);
        Assert.Null(at.LibraryName);
        Assert.Equal(2, at.UnderRoot?.Id); // the folder it's under, to name in the question
        Assert.Equal(@"D:\Phone\Camera", at.FolderPath);
    }

    [Fact]
    public void AnotherLibrarysFolder_IsNamed()
    {
        var work = MoveDestination.Classify(@"F:\Work\Q3", Roots, Others);
        Assert.Equal((MoveDestinationKind.OtherLibrary, "Work", @"F:\Work\Q3"), (work.Kind, work.LibraryName, work.FolderPath));
        Assert.Equal("Archive", MoveDestination.Classify(@"G:\Old", Roots, Others).LibraryName);
        // Under this library's folder without subfolders, but indexed by another library: that one is named.
        Assert.Equal("Work", MoveDestination.Classify(@"D:\Phone\Shared\x", Roots, Others).LibraryName);
    }

    [Fact]
    public void AnotherLibrarysFolderWithoutSubfolders_DoesntHoldItsSubfolder()
    {
        Assert.Equal(MoveDestinationKind.NoLibrary, MoveDestination.Classify(@"G:\Old\Sub", Roots, Others).Kind);
    }

    [Theory]
    [InlineData(@"C:\Photos2")]       // only starts the same
    [InlineData(@"C:\")]
    [InlineData(@"H:\Elsewhere")]
    public void AFolderInNoLibrary(string folder) =>
        Assert.Equal(MoveDestinationKind.NoLibrary, MoveDestination.Classify(folder, Roots, Others).Kind);

    [Fact]
    public void InThisLibraryAndAnother_IsThisLibrarys()
    {
        var both = new OtherLibrary("Also", [new LibraryFolder(@"C:\Photos", Recursive: true)]);

        Assert.Equal(MoveDestinationKind.ThisLibrary, MoveDestination.Classify(@"C:\Photos\x", Roots, [both]).Kind);
    }

    [Fact]
    public void WithoutOtherLibraries_ItsNoLibrary()
    {
        Assert.Equal(MoveDestinationKind.NoLibrary, MoveDestination.Classify(@"F:\Work", Roots, null).Kind);
    }
}
