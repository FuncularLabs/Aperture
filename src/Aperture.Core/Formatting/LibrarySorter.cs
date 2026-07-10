using Aperture.Core.Models;
using Aperture.Core.Settings;

namespace Aperture.Core.Formatting;

/// <summary>
/// Applies a multi-level sort to library rows. Sort tokens: <c>date</c>, <c>name</c>,
/// <c>size</c>, <c>alias</c>, <c>camera</c>, <c>ext</c>, <c>kind</c>. Unknown tokens are
/// ignored. A final tiebreak on item id keeps the order deterministic.
/// </summary>
public static class LibrarySorter
{
    public static void Sort(List<LibraryRow> rows, IReadOnlyList<SortLevel> levels)
    {
        if (levels.Count == 0)
            return;
        rows.Sort((a, b) => Compare(a, b, levels));
    }

    /// <summary>
    /// Sorts with a primary date-section key (newest section first) and the
    /// multi-level sort applied within each section.
    /// </summary>
    public static void SortSectioned(
        List<LibraryRow> rows, Func<LibraryRow, long> sectionKey, IReadOnlyList<SortLevel> levels)
    {
        rows.Sort((a, b) =>
        {
            var c = sectionKey(b).CompareTo(sectionKey(a)); // newest section first
            return c != 0 ? c : Compare(a, b, levels);
        });
    }

    private static int Compare(LibraryRow a, LibraryRow b, IReadOnlyList<SortLevel> levels)
    {
        foreach (var level in levels)
        {
            var c = CompareBy(a, b, level.Token);
            if (level.Descending)
                c = -c;
            if (c != 0)
                return c;
        }
        return a.Item.Id.CompareTo(b.Item.Id);
    }

    private static int CompareBy(LibraryRow a, LibraryRow b, string token) => token.ToLowerInvariant() switch
    {
        "date" => a.Item.BestDate.CompareTo(b.Item.BestDate),
        "name" => string.Compare(a.Item.FileName, b.Item.FileName, StringComparison.OrdinalIgnoreCase),
        "size" => a.Item.SizeBytes.CompareTo(b.Item.SizeBytes),
        "alias" => string.Compare(a.RootAlias, b.RootAlias, StringComparison.OrdinalIgnoreCase),
        "camera" => string.Compare(a.Item.Camera ?? "", b.Item.Camera ?? "", StringComparison.OrdinalIgnoreCase),
        "ext" => string.Compare(a.Item.Ext, b.Item.Ext, StringComparison.OrdinalIgnoreCase),
        "kind" => a.Item.Kind.CompareTo(b.Item.Kind),
        _ => 0,
    };

    /// <summary>Tokens the sort UI can offer.</summary>
    public static readonly string[] KnownTokens = ["date", "name", "size", "alias", "camera", "ext", "kind"];
}
