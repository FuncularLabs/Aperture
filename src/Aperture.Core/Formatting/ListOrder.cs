namespace Aperture.Core.Formatting;

/// <summary>
/// A list the user has dragged into an order of their own (the libraries, a library's top-level folders): the ids in
/// that order first, then anything added since, alphabetically. Without an order, the list is alphabetical.
/// </summary>
public static class ListOrder
{
    /// <summary>
    /// <paramref name="items"/> in <paramref name="order"/> (ids no longer present are skipped, a repeat counts once),
    /// then the items the order doesn't name, by <paramref name="name"/> with <paramref name="names"/> (the current
    /// culture, ignoring case, if not given); items of the same name keep the order they came in.
    /// </summary>
    public static List<T> Apply<T, TId>(
        IEnumerable<T> items, IReadOnlyList<TId>? order, Func<T, TId> id, Func<T, string> name,
        IComparer<string>? names = null)
        where TId : notnull
    {
        var all = items.ToList();
        var ranks = new Dictionary<TId, int>();
        foreach (var ordered in order ?? [])
            ranks.TryAdd(ordered, ranks.Count); // a repeat keeps its first place
        var byName = names ?? StringComparer.CurrentCultureIgnoreCase;
        return
        [
            .. all.Where(t => ranks.ContainsKey(id(t))).OrderBy(t => ranks[id(t)]),
            .. all.Where(t => !ranks.ContainsKey(id(t))).OrderBy(name, byName),
        ];
    }

    /// <summary>
    /// <paramref name="ids"/> with <paramref name="id"/> moved to <paramref name="toIndex"/>, an index into the list
    /// without it (clamped to its ends). An id not in the list leaves it as it is.
    /// </summary>
    public static List<TId> Move<TId>(IReadOnlyList<TId> ids, TId id, int toIndex) where TId : notnull
    {
        var moved = ids.ToList();
        var from = moved.IndexOf(id);
        if (from < 0)
            return moved;
        moved.RemoveAt(from);
        moved.Insert(Math.Clamp(toIndex, 0, moved.Count), id);
        return moved;
    }

    /// <summary>
    /// <paramref name="ids"/> with <paramref name="id"/> dropped in <paramref name="slot"/>, a gap among all the rows
    /// with it still in place (0 above the first; the row count below the last), as a drag gives: a gap below the item
    /// is one place less once it's lifted out, and the gaps either side of it are its own place.
    /// </summary>
    public static List<TId> MoveToSlot<TId>(IReadOnlyList<TId> ids, TId id, int slot) where TId : notnull
    {
        var from = ids.ToList().IndexOf(id);
        return Move(ids, id, from >= 0 && slot > from ? slot - 1 : slot);
    }
}
