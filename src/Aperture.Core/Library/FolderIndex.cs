using Aperture.Core.Models;

namespace Aperture.Core.Library;

/// <summary>
/// Every folder's subfolders for the folder tree, worked out in one pass over the library's rows: what
/// <see cref="FolderTree.Subfolders"/> lists for a folder, without reading every row again for each folder shown.
/// Immutable once built, so it can be built off the UI thread and read on it.
/// </summary>
public sealed class FolderIndex
{
    /// <summary>An index of nothing, for a library whose rows haven't been read yet.</summary>
    public static FolderIndex Empty { get; } = new([]);

    private static readonly FolderEntry[] None = [];

    private readonly Dictionary<(long RootId, string RelDir), FolderEntry[]> _levels;

    /// <summary>How many indexes this thread has built (for tests: the window builds its index off the UI thread).</summary>
    [ThreadStatic]
    internal static int BuildCalls;

    private FolderIndex(Dictionary<(long RootId, string RelDir), FolderEntry[]> levels) => _levels = levels;

    /// <summary>The subfolders of <paramref name="relDir"/> in root <paramref name="rootId"/>, as the tree lists them.</summary>
    public IReadOnlyList<FolderEntry> Subfolders(long rootId, string relDir) =>
        _levels.TryGetValue((rootId, relDir), out var entries) ? entries : None;

    /// <summary>
    /// Builds the index from the rows the library shows and its hidden subfolders, by the rules of
    /// <see cref="FolderTree.Subfolders"/>: a folder is listed if it holds a shown item or leads to a hidden folder; a
    /// hidden folder is listed unticked with its own count and nothing to expand; names match whatever their case, and
    /// a folder is named as it was first seen.
    /// </summary>
    public static FolderIndex Build(IReadOnlyList<LibraryRow> rows, IReadOnlyList<HiddenFolder> hidden)
    {
        BuildCalls++;
        var levels = new Dictionary<(long RootId, string RelDir), Level>(KeyComparer.Instance);

        Level LevelOf(long rootId, string relDir)
        {
            if (!levels.TryGetValue((rootId, relDir), out var level))
                levels[(rootId, relDir)] = level = new Level();
            return level;
        }

        // Each row counts once in every folder above it: in the root, in its first folder, and so on down to its own.
        foreach (var row in rows)
        {
            var dir = Path.GetDirectoryName(row.Item.RelPath) ?? "";
            if (dir.Length == 0)
                continue;
            var segments = dir.Split(Path.DirectorySeparatorChar);
            var parent = "";
            for (var i = 0; i < segments.Length; i++)
            {
                var level = LevelOf(row.Item.RootId, parent);
                var deeper = i < segments.Length - 1;
                if (level.Shown.TryGetValue(segments[i], out var seen))
                {
                    seen.Count++;
                    seen.HasChildren |= deeper;
                }
                else
                {
                    level.Shown[segments[i]] = seen = new Seen(Join(parent, segments[i]), 1, deeper);
                }
                parent = seen.RelDir;
            }
        }

        // Then the hidden folders, after every row as there: listed where they are, and a way to them above.
        foreach (var h in hidden)
        {
            if (h.ItemCount == 0 || h.RelDir.Length == 0)
                continue;
            var segments = h.RelDir.Split(Path.DirectorySeparatorChar);
            var parent = "";
            for (var i = 0; i < segments.Length; i++)
            {
                var level = LevelOf(h.RootId, parent);
                if (i == segments.Length - 1)
                {
                    level.Hidden[segments[i]] = h; // the hidden folder itself
                    break;
                }
                if (level.Shown.TryGetValue(segments[i], out var seen))
                    seen.HasChildren = true;
                else
                    level.Shown[segments[i]] = seen = new Seen(Join(parent, segments[i]), 0, true); // the way to it
                parent = seen.RelDir;
            }
        }

        return new FolderIndex(levels.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Shown.Where(s => !kv.Value.Hidden.ContainsKey(s.Key))
                .Select(s => new FolderEntry(s.Key, s.Value.RelDir, s.Value.Count, s.Value.HasChildren, IsShown: true))
                .Concat(kv.Value.Hidden.Values.Select(h =>
                    new FolderEntry(Path.GetFileName(h.RelDir), h.RelDir, h.ItemCount, HasChildren: false, IsShown: false)))
                .OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            KeyComparer.Instance));
    }

    private static string Join(string parent, string child) => parent.Length == 0 ? child : Path.Combine(parent, child);

    private sealed class Level
    {
        public Dictionary<string, Seen> Shown { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HiddenFolder> Hidden { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Seen(string relDir, int count, bool hasChildren)
    {
        public string RelDir { get; } = relDir;
        public int Count { get; set; } = count;
        public bool HasChildren { get; set; } = hasChildren;
    }

    // A folder is the same folder whatever the case of its path, as everywhere in the tree.
    private sealed class KeyComparer : IEqualityComparer<(long RootId, string RelDir)>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals((long RootId, string RelDir) x, (long RootId, string RelDir) y) =>
            x.RootId == y.RootId && StringComparer.OrdinalIgnoreCase.Equals(x.RelDir, y.RelDir);

        public int GetHashCode((long RootId, string RelDir) key) =>
            HashCode.Combine(key.RootId, StringComparer.OrdinalIgnoreCase.GetHashCode(key.RelDir));
    }
}
