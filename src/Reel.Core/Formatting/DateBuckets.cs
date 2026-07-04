using System.Globalization;

namespace Reel.Core.Formatting;

public enum BucketMode
{
    Week,
    Month,
    Year,
}

/// <summary>The date section an item belongs to.</summary>
public readonly record struct DateBucket(long Key, string Label);

/// <summary>
/// Groups items into date sections and picks a sensible granularity based on how
/// densely the library is populated, so the first screen shows a couple of
/// sections rather than thousands of loose tiles.
/// </summary>
public static class DateBuckets
{
    /// <summary>
    /// Picks week/month/year buckets from item density. Heavy shooters (many per
    /// week) get week sections; sparse libraries collapse to years.
    /// </summary>
    public static BucketMode ChooseMode(IReadOnlyList<DateTime> dates)
    {
        if (dates.Count < 8)
            return BucketMode.Year;

        var min = dates[0];
        var max = dates[0];
        foreach (var d in dates)
        {
            if (d < min) min = d;
            if (d > max) max = d;
        }

        var weeks = Math.Max(1.0, (max - min).TotalDays / 7.0);
        var perWeek = dates.Count / weeks;

        return perWeek switch
        {
            >= 40 => BucketMode.Week,
            >= 8 => BucketMode.Month,
            _ => BucketMode.Year,
        };
    }

    /// <summary>Maps a date to its bucket. Keys increase with time so newest sorts first when descending.</summary>
    public static DateBucket Bucket(DateTime date, BucketMode mode)
    {
        switch (mode)
        {
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
