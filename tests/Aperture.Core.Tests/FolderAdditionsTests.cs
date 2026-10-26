using Aperture.App.ViewModels;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// Adding folders asks once whether to include their subfolders (Paul, 2026-10-26) — for the folders that are
/// actually new, and not at all when none is.
/// </summary>
public class FolderAdditionsTests
{
    [Fact]
    public void AsksOnce_ForAllTheNewFolders_AndAddsThemWithTheAnswer()
    {
        using var dir = new TempDir();
        var trips = Directory.CreateDirectory(dir.Combine("Trips")).FullName;
        var pets = Directory.CreateDirectory(dir.Combine("Pets")).FullName;
        var asked = new List<IReadOnlyList<string>>();

        var plan = FolderAdditions.Plan([trips, pets], alreadyAdded: [], folders => { asked.Add(folders); return false; });

        Assert.Equal([trips, pets], Assert.Single(asked));
        Assert.Equal([new FolderToAdd(trips, "Trips", IncludeSubfolders: false), new FolderToAdd(pets, "Pets", IncludeSubfolders: false)], plan);
    }

    [Fact]
    public void Cancel_AddsNothing()
    {
        using var dir = new TempDir();
        var trips = Directory.CreateDirectory(dir.Combine("Trips")).FullName;

        Assert.Empty(FolderAdditions.Plan([trips], alreadyAdded: [], _ => null));
    }

    [Fact]
    public void FoldersAlreadyAddedOrGone_AreSkipped_WithoutAsking()
    {
        using var dir = new TempDir();
        var trips = Directory.CreateDirectory(dir.Combine("Trips")).FullName;
        var asked = 0;

        var plan = FolderAdditions.Plan([trips.ToUpperInvariant(), dir.Combine("Gone")], alreadyAdded: [trips],
            _ => { asked++; return true; });

        Assert.Empty(plan);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void AFolderChosenTwice_IsAskedAboutAndAddedOnce()
    {
        using var dir = new TempDir();
        var trips = Directory.CreateDirectory(dir.Combine("Trips")).FullName;
        var asked = new List<IReadOnlyList<string>>();

        var plan = FolderAdditions.Plan([trips, trips + Path.DirectorySeparatorChar, trips.ToLowerInvariant()], alreadyAdded: [],
            folders => { asked.Add(folders); return true; });

        Assert.Equal([trips], Assert.Single(asked));
        Assert.Equal([new FolderToAdd(trips, "Trips", IncludeSubfolders: true)], plan);
    }
}
