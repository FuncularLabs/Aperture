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
        Assert.Equal(BucketMode.Year, DateBuckets.ChooseMode(dates));
    }

    [Fact]
    public void Buckets_ChooseWeek_WhenDense()
    {
        // 300 photos across ~4 weeks => ~75/week => week buckets.
        var start = new DateTime(2024, 6, 1);
        var dates = Enumerable.Range(0, 300).Select(i => start.AddHours(i * 2)).ToList();
        Assert.Equal(BucketMode.Week, DateBuckets.ChooseMode(dates));
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
