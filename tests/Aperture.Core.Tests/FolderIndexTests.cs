using System.Collections;
using Aperture.Core.Library;
using Aperture.Core.Models;

namespace Aperture.Core.Tests;

/// <summary>
/// The folder tree's index, built in one pass over the rows (0.9.1 batch, item 3: a folder with many subfolders froze
/// the window, as the tree read every row again for each folder it showed). It must list exactly what
/// <see cref="FolderTree.Subfolders"/> lists for the same rows, for every folder.
/// </summary>
public class FolderIndexTests
{
    private static LibraryRow Row(string relPath, long rootId) => new()
    {
        Item = new MediaItem { RootId = rootId, RelPath = relPath, FileName = Path.GetFileName(relPath), Ext = ".jpg" },
        RootAlias = "Photos",
        RootPath = @"C:\Photos",
    };

    private static string Show(IEnumerable<FolderEntry> entries) =>
        string.Join("; ", entries.Select(e => $"{e.Name}|{e.RelDir}|{e.Count}|{e.HasChildren}|{e.IsShown}"));

    // Walks the whole tree the way the window does (from each root, into every folder listed) and checks the index
    // against FolderTree.Subfolders at every folder, and at a folder neither lists.
    private static void AssertSame(IReadOnlyList<LibraryRow> rows, IReadOnlyList<HiddenFolder> hidden, params long[] roots)
    {
        var index = FolderIndex.Build(rows, hidden);
        var folders = 0;
        foreach (var root in roots)
        {
            var pending = new Stack<string>([""]);
            while (pending.Count > 0)
            {
                var relDir = pending.Pop();
                var expected = FolderTree.Subfolders(rows, hidden, root, relDir);
                Assert.Equal(Show(expected), Show(index.Subfolders(root, relDir)));
                folders++;
                foreach (var e in expected.Where(e => e.HasChildren))
                    pending.Push(e.RelDir);
            }
            Assert.Empty(index.Subfolders(root, @"no such\folder"));
        }
        Assert.True(folders > roots.Length, "the walk went into no folder: the test proves nothing");
    }

    [Fact]
    public void MatchesSubfolders_ForTheTreeTestsShapes()
    {
        var rows = new List<LibraryRow>
        {
            Row("a.jpg", 1), Row(@"2024\x.jpg", 1), Row(@"2024\06\y.jpg", 1), Row(@"2024\06\z.jpg", 1),
            Row(@"2023\w.jpg", 1), Row(@"Trip 2\x.jpg", 1), Row(@"Trip\Day 2\a.jpg", 1),
            Row(@"2024\x.jpg", 2), Row(@"Elsewhere\deep\er\b.jpg", 2),
        };
        HiddenFolder[] hidden =
        [
            new(1, "Trip", 4), new(1, @"Trip\Day 1", 2), new(1, @"Archive\Old", 3), new(1, "Gone", 0),
            new(2, @"Elsewhere\deep\er\est", 1), new(1, "2023", 1),
        ];

        AssertSame(rows, hidden, 1, 2);
    }

    [Fact]
    public void MatchesSubfolders_WhereTheCaseOfAFolderVaries()
    {
        // The same folder seen as "Trip" and as "trip" (a rename that only changed the case): one entry, named as first seen.
        var rows = new List<LibraryRow>
        {
            Row(@"Trip\a.jpg", 1), Row(@"trip\b.jpg", 1), Row(@"TRIP\Day\c.jpg", 1), Row(@"trip\day\d.jpg", 1),
        };
        HiddenFolder[] hidden = [new(1, @"trip\DAY\night", 2), new(1, @"TRIP\other", 1)];

        AssertSame(rows, hidden, 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MatchesSubfolders_ForEveryFolder_OfGeneratedTrees(int seed)
    {
        var random = new Random(seed);
        string[] names = ["A", "a", "B", "Trip", "Trip 2", "2024", "06", "x y"];
        string Dir(int depth) => string.Join('\\', Enumerable.Range(0, depth).Select(_ => names[random.Next(names.Length)]));

        var rows = new List<LibraryRow>();
        for (var i = 0; i < 400; i++)
        {
            var dir = Dir(random.Next(0, 5));
            rows.Add(Row(dir.Length == 0 ? $"f{i}.jpg" : $@"{dir}\f{i}.jpg", random.Next(1, 3)));
        }
        var hidden = Enumerable.Range(0, 12)
            .Select(_ => new HiddenFolder(random.Next(1, 3), Dir(random.Next(1, 4)), random.Next(0, 4)))
            .ToList();

        AssertSame(rows, hidden, 1, 2);
    }

    [Fact]
    public void ReadsTheRowsOnce()
    {
        var rows = new CountingRows([Row(@"a\b\c\1.jpg", 1), Row(@"a\b\2.jpg", 1), Row(@"a\3.jpg", 1)]);

        var index = FolderIndex.Build(rows, []);
        Assert.Equal(1, rows.Passes);

        // Listing folders, however deep and however often, reads the index, not the rows.
        index.Subfolders(1, "");
        index.Subfolders(1, "a");
        index.Subfolders(1, @"a\b");
        index.Subfolders(1, @"a\b");
        Assert.Equal(1, rows.Passes);
        Assert.Equal(@"b|a\b|2|True|True", Show(index.Subfolders(1, "a")));
    }

    [Fact]
    public void AnEmptyLibrary_ListsNothing()
    {
        var index = FolderIndex.Build([], []);

        Assert.Empty(index.Subfolders(1, ""));
        Assert.Empty(FolderIndex.Empty.Subfolders(1, "")); // what a view model has before its rows are read
    }

    // A row list that counts how many times it is walked (by enumeration, or by index from the start).
    private sealed class CountingRows(List<LibraryRow> rows) : IReadOnlyList<LibraryRow>
    {
        public int Passes { get; private set; }

        public LibraryRow this[int index]
        {
            get
            {
                if (index == 0)
                    Passes++;
                return rows[index];
            }
        }

        public int Count => rows.Count;

        public IEnumerator<LibraryRow> GetEnumerator()
        {
            Passes++;
            return rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
