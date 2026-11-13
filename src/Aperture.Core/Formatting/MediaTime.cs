namespace Aperture.Core.Formatting;

/// <summary>
/// Quick view's video time (0.9.2 V10): "elapsed / length" in whole seconds gone by, as <c>m:ss</c>, or <c>h:mm:ss</c>
/// on both sides when the length is an hour or more; the elapsed time alone when the length isn't known.
/// </summary>
public static class MediaTime
{
    /// <summary>The time shown before a video has opened (its length not known yet).</summary>
    public const string Pending = "0:00 / –:––";

    public static string Format(TimeSpan elapsed, TimeSpan? length)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        if (length is not { } total)
            return Clock(elapsed, elapsed.TotalHours >= 1);
        if (total < TimeSpan.Zero)
            total = TimeSpan.Zero;
        if (elapsed > total)
            elapsed = total;
        var hours = total.TotalHours >= 1;
        return Clock(elapsed, hours) + " / " + Clock(total, hours);
    }

    private static string Clock(TimeSpan time, bool hours)
    {
        var seconds = (long)Math.Floor(time.TotalSeconds);
        var (h, m, s) = (seconds / 3600, seconds / 60 % 60, seconds % 60);
        return hours ? $"{h}:{m:00}:{s:00}" : $"{seconds / 60}:{s:00}";
    }
}
