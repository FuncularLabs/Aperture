namespace Reel.Core.Annotations;

/// <summary>A tag with how many items carry it and when it was last applied.</summary>
public readonly record struct TagUsage(string Name, int Count, long LastUsedTicks);

/// <summary>
/// Picks the tags to offer as search quick-picks. When usage is highly skewed — a
/// few tags applied to far more items than the rest — the popular ones are the most
/// useful shortcut, so we rank by count. When usage is fairly even, no tag stands
/// out by popularity, so recency (what you've been tagging lately) is the better cue.
/// </summary>
public static class TagQuickPicks
{
    public static (List<string> Tags, bool ByUsage) Select(IReadOnlyList<TagUsage> usage, int max)
    {
        if (usage.Count == 0)
            return ([], false);

        var byUsage = IsSkewed([.. usage.Select(u => u.Count)]);
        var ordered = byUsage
            ? usage.OrderByDescending(u => u.Count).ThenByDescending(u => u.LastUsedTicks)
            : usage.OrderByDescending(u => u.LastUsedTicks).ThenByDescending(u => u.Count);

        return (ordered.Take(max).Select(u => u.Name).ToList(), byUsage);
    }

    /// <summary>
    /// True when a few tags dominate: the top ~20% of tags hold more than 60% of all
    /// applications AND the most-used tag is at least 3× the median. Needs enough
    /// tags to have a meaningful distribution.
    /// </summary>
    public static bool IsSkewed(IReadOnlyList<int> counts)
    {
        if (counts.Count < 4)
            return false;

        var desc = counts.OrderByDescending(c => c).ToList();
        var total = desc.Sum();
        if (total <= 0)
            return false;

        var topN = Math.Max(1, desc.Count / 5);
        var topShare = desc.Take(topN).Sum() / (double)total;
        var median = desc[desc.Count / 2];
        return topShare > 0.6 && desc[0] >= 3 * Math.Max(1, median);
    }
}
