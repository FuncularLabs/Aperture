using Aperture.Core.Formatting;

namespace Aperture.Core.Tests;

/// <summary>
/// Which date sections the user expanded or collapsed, kept per grouping: the grouping can now change with the window's
/// width (round 22 of the multiple-libraries review).
/// </summary>
public class SectionExpansionTests
{
    [Fact]
    public void ADay_AndTheWeekStartingOnIt_AreRememberedApart()
    {
        var monday = DateBuckets.Bucket(new DateTime(2024, 3, 4), BucketMode.Day).Key;
        Assert.Equal(monday, DateBuckets.Bucket(new DateTime(2024, 3, 6), BucketMode.Week).Key); // one key, two sections

        var expansion = SectionExpansion.None.With(BucketMode.Day, new Dictionary<long, bool> { [monday] = false });

        Assert.False(expansion.For(BucketMode.Day)[monday]);
        Assert.Empty(expansion.For(BucketMode.Week));
    }

    [Fact]
    public void AGroupingsToggles_OutlastAnotherGroupingInBetween()
    {
        var expansion = SectionExpansion.None
            .With(BucketMode.Year, new Dictionary<long, bool> { [2023] = false })
            .With(BucketMode.Month, new Dictionary<long, bool> { [2023 * 12L + 4] = true });

        Assert.False(expansion.For(BucketMode.Year)[2023]);
        Assert.True(expansion.For(BucketMode.Month)[2023 * 12L + 4]);
    }

    [Fact]
    public void AGroupingsSections_ReplaceWhatItHad_InACopy()
    {
        var sections = new Dictionary<long, bool> { [2023] = false };
        var first = SectionExpansion.None.With(BucketMode.Year, sections);
        var second = first.With(BucketMode.Year, new Dictionary<long, bool> { [2024] = true });
        sections[2022] = true;

        Assert.Equal([2024L], second.For(BucketMode.Year).Keys);
        Assert.Equal([2023L], first.For(BucketMode.Year).Keys); // a build still running keeps what it was given
        Assert.Empty(SectionExpansion.None.For(BucketMode.Year));
    }
}
