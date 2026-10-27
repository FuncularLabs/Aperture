using Aperture.Core.Models;

namespace Aperture.Core.Library;

/// <summary>
/// One subfolder in the folder tree: its name, path relative to the root, how many items it holds (all levels),
/// whether it has subfolders of its own to expand, and whether it's shown (ticked) or hidden.
/// </summary>
public sealed record FolderEntry(string Name, string RelDir, int Count, bool HasChildren, bool IsShown);

/// <summary>Works out a folder's subfolders for the tree, from the library's rows and its hidden folders.</summary>
public static class FolderTree
{
    /// <summary>
    /// The subfolders of <paramref name="relDir"/> in root <paramref name="rootId"/>, by name, last first. A
    /// subfolder appears if it holds a shown item, or leads to a hidden folder (so that folder can be shown again).
    /// A hidden subfolder is listed unticked, with its own item count and nothing to expand; one holding no items
    /// any more (renamed or deleted on disk) isn't listed.
    /// </summary>
    public static List<FolderEntry> Subfolders(
        IReadOnlyList<LibraryRow> rows, IReadOnlyList<HiddenFolder> hidden, long rootId, string relDir)
    {
        var shown = new Dictionary<string, (string RelDir, int Count, bool HasChildren)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (r.Item.RootId != rootId)
                continue;
            var dir = Path.GetDirectoryName(r.Item.RelPath) ?? "";
            if (!IsUnder(dir, relDir) || dir.Equals(relDir, StringComparison.OrdinalIgnoreCase))
                continue;
            var child = FirstSegmentUnder(dir, relDir);
            var childRel = relDir.Length == 0 ? child : Path.Combine(relDir, child);
            var deeper = !dir.Equals(childRel, StringComparison.OrdinalIgnoreCase);
            shown[child] = shown.TryGetValue(child, out var v)
                ? (v.RelDir, v.Count + 1, v.HasChildren || deeper)
                : (childRel, 1, deeper);
        }

        var hiddenHere = new Dictionary<string, HiddenFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hidden)
        {
            if (h.RootId != rootId || h.ItemCount == 0 || !IsUnder(h.RelDir, relDir)
                || h.RelDir.Equals(relDir, StringComparison.OrdinalIgnoreCase))
                continue;
            var child = FirstSegmentUnder(h.RelDir, relDir);
            var childRel = relDir.Length == 0 ? child : Path.Combine(relDir, child);
            if (h.RelDir.Equals(childRel, StringComparison.OrdinalIgnoreCase))
                hiddenHere[child] = h; // the hidden folder itself
            else
                shown[child] = shown.TryGetValue(child, out var v) ? (v.RelDir, v.Count, true) : (childRel, 0, true); // the way to it
        }

        return shown.Where(kv => !hiddenHere.ContainsKey(kv.Key))
            .Select(kv => new FolderEntry(kv.Key, kv.Value.RelDir, kv.Value.Count, kv.Value.HasChildren, IsShown: true))
            .Concat(hiddenHere.Values.Select(h =>
                new FolderEntry(Path.GetFileName(h.RelDir), h.RelDir, h.ItemCount, HasChildren: false, IsShown: false)))
            .OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// How many of a root's items are hidden: the items of its outermost hidden folders (a hidden folder inside
    /// another hidden one is already counted in it). A root's count in the tree leaves these out.
    /// </summary>
    public static int HiddenItemCount(IReadOnlyList<HiddenFolder> hidden, long rootId)
    {
        var ofRoot = hidden.Where(h => h.RootId == rootId).ToList();
        return ofRoot
            .Where(h => !ofRoot.Any(outer => !ReferenceEquals(outer, h) && IsUnder(h.RelDir, outer.RelDir)
                                             && !h.RelDir.Equals(outer.RelDir, StringComparison.OrdinalIgnoreCase)))
            .Sum(h => h.ItemCount);
    }

    private static bool IsUnder(string dir, string relDir) =>
        relDir.Length == 0
        || dir.Equals(relDir, StringComparison.OrdinalIgnoreCase)
        || dir.StartsWith(relDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // The first folder name in `dir` below `relDir` (dir is under relDir and not relDir itself).
    private static string FirstSegmentUnder(string dir, string relDir)
    {
        var rest = relDir.Length == 0 ? dir : dir[(relDir.Length + 1)..];
        var sep = rest.IndexOf(Path.DirectorySeparatorChar);
        return sep < 0 ? rest : rest[..sep];
    }
}
