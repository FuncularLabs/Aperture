using System.Globalization;

namespace Reel.Core.Formatting;

public enum BucketMode
{
    Day,
    Week,
    Month,
    Year,
}

/// <summary>The date section an item belongs to.</summary>
public readonly record struct DateBucket(long Key, string Label);

/// <summary>
/// Groups items into date sections. Granularity is chosen so the number of
/// sections lands near a target (~12) — coarse enough to stay scannable, fine
/// enough to be useful — rather than by a fixed density rule.
/// </summary>
public static class DateBuckets
{
    /// <summary>Preferred number of sections. The coarsest granularity nearest this wins.</summary>
    public const int TargetSections = 12;

    // Coarsest first, so ties (equal distance to target) favour fewer, broader sections.
    private static readonly BucketMode[] CoarseToFine = [BucketMode.Year, BucketMode.Month, BucketMode.Week, BucketMode.Day];

    /// <summary>Picks the granularity whose section count is closest to <see cref="TargetSections"/>.</summary>
    public static BucketMode ChooseMode(IReadOnlyList<DateTime> dates)
    {
        if (dates.Count == 0)
            return BucketMode.Year;

        var best = BucketMode.Year;
        var bestDistance = int.MaxValue;

        foreach (var mode in CoarseToFine)
        {
            var count = CountSections(dates, mode);
            var distance = Math.Abs(count - TargetSections);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = mode;
            }
        }
        return best;
    }

    private static int CountSections(IReadOnlyList<DateTime> dates, BucketMode mode)
    {
        var keys = new HashSet<long>();
        foreach (var date in dates)
            keys.Add(Bucket(date, mode).Key);
        return keys.Count;
    }

    /// <summary>Maps a date to its bucket. Keys increase with time so newest sorts first when descending.</summary>
    public static DateBucket Bucket(DateTime date, BucketMode mode)
    {
        switch (mode)
        {
            case BucketMode.Day:
                return new DateBucket(date.Date.Ticks, date.ToString("dddd, MMM d, yyyy", CultureInfo.CurrentCulture));

            case BucketMode.Week:
                var monday = StartOfWeek(date);
                return new DateBucket(monday.Ticks, $"Week of {monday:MMM d, yyyy}");

            case BucketMode.Month:
                return new DateBucket(date.Year * 12L + date.Month, date.ToString("MMMM yyyy", CultureInfo.CurrentCulture));

            default:
                return new DateBucket(date.Year, date.Year.ToString());
        }
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.Date.AddDays(-diff);
    }
}
