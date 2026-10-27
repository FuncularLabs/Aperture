using System.Globalization;

namespace Aperture.Core.Formatting;

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
/// Groups items into date sections. The granularity is chosen so sections don't leave their rows mostly empty in a grid
/// of the given width, and — among those that don't — so the number of sections lands near a target (~12): coarse enough
/// to stay scannable, fine enough to be useful.
/// </summary>
public static class DateBuckets
{
    /// <summary>Preferred number of sections. Among qualifying granularities, the one nearest this wins (coarser on a tie).</summary>
    public const int TargetSections = 12;

    /// <summary>
    /// How full a granularity's sections must be, on average, to qualify: each section's tiles over the tile slots of the
    /// rows it takes (one tile in a 12-across grid fills 1/12 of its row), averaged over the sections.
    /// </summary>
    public const double MinRowFill = 0.5;

    /// <summary>
    /// Picks the granularity for <paramref name="dates"/> in a grid <paramref name="tilesPerRow"/> tiles across: among those
    /// whose sections fill at least <see cref="MinRowFill"/> of their rows on average, the one whose section count is
    /// nearest <see cref="TargetSections"/>; if none does, the one that fills its rows best. Coarser wins a tie.
    /// </summary>
    public static BucketMode ChooseMode(IReadOnlyList<DateTime> dates, int tilesPerRow) =>
        DateGroupings.Of(dates).ChooseMode(tilesPerRow);

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

/// <summary>
/// The sizes of the sections each granularity makes of a set of dates — all <see cref="DateBuckets.ChooseMode"/> weighs, so
/// a grid that changes width can choose again without the dates, and leave its view alone when the choice stands.
/// </summary>
public sealed class DateGroupings
{
    // Coarsest first, so ties favour fewer, broader sections.
    private static readonly BucketMode[] CoarseToFine = [BucketMode.Year, BucketMode.Month, BucketMode.Week, BucketMode.Day];

    private readonly Dictionary<BucketMode, int[]> _sizes;

    private DateGroupings(Dictionary<BucketMode, int[]> sizes) => _sizes = sizes;

    /// <summary>Measures how <paramref name="dates"/> group by each granularity.</summary>
    public static DateGroupings Of(IReadOnlyList<DateTime> dates) =>
        new(CoarseToFine.ToDictionary(mode => mode, mode => SectionSizes(dates, mode)));

    /// <summary>How many sections <paramref name="mode"/> makes of the dates.</summary>
    public int SectionCount(BucketMode mode) => _sizes[mode].Length;

    /// <summary>The granularity for a grid <paramref name="tilesPerRow"/> tiles across (see <see cref="DateBuckets.ChooseMode"/>).</summary>
    public BucketMode ChooseMode(int tilesPerRow)
    {
        if (_sizes[BucketMode.Year].Length == 0)
            return BucketMode.Year; // no dates
        var across = Math.Max(1, tilesPerRow);

        BucketMode? nearest = null;
        var nearestDistance = int.MaxValue;
        var fullest = BucketMode.Year;
        var fullestFill = -1.0;
        foreach (var mode in CoarseToFine)
        {
            var sizes = _sizes[mode];
            var fill = sizes.Average(n => n / (Math.Ceiling(n / (double)across) * across));
            if (fill > fullestFill)
            {
                fullestFill = fill;
                fullest = mode;
            }
            var distance = Math.Abs(sizes.Length - DateBuckets.TargetSections);
            if (fill >= DateBuckets.MinRowFill && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = mode;
            }
        }
        return nearest ?? fullest;
    }

    private static int[] SectionSizes(IReadOnlyList<DateTime> dates, BucketMode mode)
    {
        var sizes = new Dictionary<long, int>();
        foreach (var date in dates)
        {
            var key = DateBuckets.Bucket(date, mode).Key;
            sizes[key] = sizes.GetValueOrDefault(key) + 1;
        }
        return [.. sizes.Values];
    }
}
