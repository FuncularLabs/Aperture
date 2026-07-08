namespace Reel.Core.Annotations;

/// <summary>
/// Pure tag/note merge rules shared by the Tags &amp; Notes dialog. Kept in Core so
/// the (fiddly) multi-selection semantics can be unit-tested without any UI.
///
/// The dialog shows an <em>aggregate</em> of the selection, so edits are expressed
/// as deltas — a set of tags to add to every item and a set to remove from every
/// item — rather than an absolute tag list. Notes are edited for the items that
/// share the majority note (empty counts as "matches"); items carrying a different
/// note are left untouched.
/// </summary>
public static class AnnotationMerge
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// One item's resulting tags: its originals, plus <paramref name="added"/>,
    /// minus <paramref name="removed"/> (all case-insensitive, order preserved,
    /// de-duplicated). Removal wins over a same-tag addition.
    /// </summary>
    public static List<string> MergeTags(
        IEnumerable<string> original, IEnumerable<string> added, IEnumerable<string> removed)
    {
        var rem = new HashSet<string>(removed.Select(Trim).Where(NotEmpty), Ci);
        var result = new List<string>();

        void AddUnique(string tag)
        {
            tag = tag.Trim();
            if (tag.Length == 0 || rem.Contains(tag) || result.Any(r => Ci.Equals(r, tag)))
                return;
            result.Add(tag);
        }

        foreach (var t in original)
            AddUnique(t);
        foreach (var t in added)
            AddUnique(t);
        return result;
    }

    /// <summary>
    /// One item's resulting note. If the note field wasn't edited, the original is
    /// kept. Otherwise the edited value is applied only when the item is part of the
    /// editable group — its note is empty, or equals the majority note; items with a
    /// different note keep theirs.
    /// </summary>
    public static string MergeNote(string? originalNote, string? editedNote, string? majorityNote, bool noteChanged)
    {
        var original = originalNote ?? "";
        if (!noteChanged)
            return original;

        var current = original.Trim();
        var majority = (majorityNote ?? "").Trim();
        var editable = current.Length == 0 || string.Equals(current, majority, StringComparison.Ordinal);
        return editable ? (editedNote ?? "") : original;
    }

    private static string Trim(string s) => s.Trim();
    private static bool NotEmpty(string s) => s.Length > 0;
}
