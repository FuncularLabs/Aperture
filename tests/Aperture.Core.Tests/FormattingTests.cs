using Aperture.Core.Formatting;
using Aperture.Core.Models;
using Aperture.Core.Settings;

namespace Aperture.Core.Tests;

public class FormattingTests
{
    private static MediaItem Item(
        string name = "IMG_1.jpg",
        DateTime? taken = null,
        DateTime? mtime = null,
        long size = 2048,
        int? w = 4000,
        int? h = 3000,
        string? camera = "Pixel 8",
        MediaKind kind = MediaKind.Image) => new()
        {
            Id = 1,
            RelPath = name,
            FileName = name,
            Ext = Path.GetExtension(name),
            SizeBytes = size,
            MTimeUtc = mtime ?? new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc),
            TakenUtc = taken,
            Width = w,
            Height = h,
            Camera = camera,
            Kind = kind,
        };

    // --- CaptionFormatter ---

    [Fact]
    public void Caption_ResolvesCommonTokens()
    {
        var item = Item(taken: new DateTime(2024, 6, 15, 14, 5, 0));
        var caption = CaptionFormatter.Format("{date:yyyy-MM-dd HH.mm} · {alias}", item, "Camera");
        Assert.Equal("2024-06-15 14.05 · Camera", caption);
    }

    [Fact]
    public void Caption_TimeFormatWithColon_IsNotSplitAsSpecifier()
    {
        var item = Item(taken: new DateTime(2024, 6, 15, 14, 5, 0));
        Assert.Equal("14:05", CaptionFormatter.Format("{date:HH:mm}", item, "X"));
    }

    [Fact]
    public void Caption_FallbackChain_UsesFirstNonEmpty()
    {
        var noExif = Item(taken: null, mtime: new DateTime(2024, 1, 2, 3, 4, 0, DateTimeKind.Utc));
        var caption = CaptionFormatter.Format("{exif.date ?? mtime : yyyy-MM-dd}", noExif, "X");
        Assert.Equal(noExif.MTimeUtc.ToLocalTime().ToString("yyyy-MM-dd"), caption);
    }

    [Fact]
    public void Caption_SizeAndDimTokens()
    {
        var item = Item(size: 3 * 1024 * 1024, w: 800, h: 600);
        Assert.Equal("3 MB / 800×600", CaptionFormatter.Format("{size} / {dim}", item, "X"));
    }

    [Fact]
    public void Caption_UnknownToken_RendersEmpty()
    {
        Assert.Equal("[]", CaptionFormatter.Format("[{bogus}]", Item(), "X"));
    }

    // --- DateBuckets ---

    [Fact]
    public void Buckets_ChooseYear_WhenSparse()
    {
        var dates = Enumerable.Range(0, 5).Select(i => new DateTime(2020 + i, 1, 1)).ToList();
        Assert.Equal(BucketMode.Year, DateBuckets.ChooseMode(dates, tilesPerRow: 8));
    }

    [Fact]
    public void Buckets_ChooseWeek_WhenDense()
    {
        // 300 photos across ~4 weeks => ~75/week => week buckets: every grouping fills its rows, and weeks come nearest
        // twelve sections.
        var start = new DateTime(2024, 6, 1);
        var dates = Enumerable.Range(0, 300).Select(i => start.AddHours(i * 2)).ToList();
        Assert.Equal(BucketMode.Week, DateBuckets.ChooseMode(dates, tilesPerRow: 8));
    }

    // Twelve photos, each on its own day — two days a week, a week every eight weeks: twelve days, six weeks, six
    // months, one year (Paul's sparse Funcular, 2026-10-27).
    private static List<DateTime> TwelveSparseDays() =>
        [.. Enumerable.Range(0, 6).SelectMany(k => new[] { new DateTime(2020, 1, 6).AddDays(56 * k), new DateTime(2020, 1, 8).AddDays(56 * k) })];

    [Fact]
    public void Buckets_SparseDaysInAWideGrid_GroupByYear()
    {
        // Twelve one-photo days are exactly twelve sections — but in a 12-across grid each fills 1/12 of its row.
        Assert.Equal(BucketMode.Year, DateBuckets.ChooseMode(TwelveSparseDays(), tilesPerRow: 12));
    }

    [Fact]
    public void Buckets_SparseDaysInAOneAcrossGrid_KeepNearestTwelve()
    {
        // One tile across fills every row, so the grid's width can't rule anything out: twelve days win, as before.
        Assert.Equal(BucketMode.Day, DateBuckets.ChooseMode(TwelveSparseDays(), tilesPerRow: 1));
    }

    [Fact]
    public void Buckets_DaysThatFillTheirRows_StayDays()
    {
        var dates = Enumerable.Range(0, 12)
            .SelectMany(day => Enumerable.Range(0, 12).Select(i => new DateTime(2024, 3, 1 + day * 2, 8 + i % 10, i, 0)))
            .ToList();

        Assert.Equal(BucketMode.Day, DateBuckets.ChooseMode(dates, tilesPerRow: 12));
    }

    [Fact]
    public void Buckets_RowFill_IsAveragedPerSection_NotPerPhoto()
    {
        // Eleven one-photo days (one a fortnight, early 2022 and 2023) and one day of 500: twelve days, twelve weeks. By
        // photo those rows are 80 % full, but eleven of the twelve sections are a single tile in a 12-across row; by year
        // (6, 5 and 500 photos) the rows are well filled.
        var singles = new[]
        {
            new DateTime(2022, 1, 3), new DateTime(2022, 1, 17), new DateTime(2022, 2, 7), new DateTime(2022, 2, 21),
            new DateTime(2022, 3, 7), new DateTime(2022, 3, 21), new DateTime(2023, 1, 9), new DateTime(2023, 1, 23),
            new DateTime(2023, 2, 6), new DateTime(2023, 2, 20), new DateTime(2023, 3, 6),
        };
        var dates = singles.Concat(Enumerable.Range(0, 500).Select(i => new DateTime(2024, 6, 15, 12, 0, 0).AddSeconds(i))).ToList();

        Assert.Equal(BucketMode.Year, DateBuckets.ChooseMode(dates, tilesPerRow: 12));
    }

    [Fact]
    public void Buckets_NoDates_GiveYears()
    {
        Assert.Equal(BucketMode.Year, DateBuckets.ChooseMode([], tilesPerRow: 12));
    }

    [Fact]
    public void Buckets_MonthKeysOrderChronologically()
    {
        var jan = DateBuckets.Bucket(new DateTime(2024, 1, 10), BucketMode.Month);
        var dec = DateBuckets.Bucket(new DateTime(2024, 12, 10), BucketMode.Month);
        Assert.True(dec.Key > jan.Key);
        Assert.Equal("January 2024", jan.Label);
    }

    // --- LibrarySorter ---

    [Fact]
    public void Sort_MultiLevel_SizeDescThenName()
    {
        var rows = new List<LibraryRow>
        {
            Row(Item(name: "b.jpg", size: 100)),
            Row(Item(name: "a.jpg", size: 200)),
            Row(Item(name: "c.jpg", size: 200)),
        };

        LibrarySorter.Sort(rows, [new SortLevel("size", true), new SortLevel("name", false)]);

        Assert.Equal(["a.jpg", "c.jpg", "b.jpg"], rows.Select(r => r.Item.FileName));
    }

    private static LibraryRow Row(MediaItem item) =>
        new() { Item = item, RootAlias = "Camera", RootPath = @"C:\x" };
}
