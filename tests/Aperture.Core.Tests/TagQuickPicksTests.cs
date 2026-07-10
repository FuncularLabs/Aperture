using Aperture.Core.Annotations;

namespace Aperture.Core.Tests;

public class TagQuickPicksTests
{
    private static TagUsage U(string name, int count, long ticks) => new(name, count, ticks);

    [Fact]
    public void Empty_ReturnsNothing()
    {
        var (tags, byUsage) = TagQuickPicks.Select([], 5);
        Assert.Empty(tags);
        Assert.False(byUsage);
    }

    [Fact]
    public void EvenUsage_RanksByRecency()
    {
        var usage = new[] { U("a", 2, 100), U("b", 2, 400), U("c", 2, 300), U("d", 2, 200) };
        var (tags, byUsage) = TagQuickPicks.Select(usage, 3);
        Assert.False(byUsage);
        Assert.Equal(["b", "c", "d"], tags); // most recent first
    }

    [Fact]
    public void SkewedUsage_RanksByCount()
    {
        var usage = new[] { U("hot", 100, 100), U("a", 1, 400), U("b", 1, 300), U("c", 1, 200), U("d", 1, 150) };
        var (tags, byUsage) = TagQuickPicks.Select(usage, 3);
        Assert.True(byUsage);
        Assert.Equal("hot", tags[0]); // the dominant tag leads
    }

    [Fact]
    public void FewTags_NotConsideredSkewed()
    {
        // Under the 4-tag threshold there isn't enough of a distribution to judge.
        Assert.False(TagQuickPicks.IsSkewed([50, 1]));
    }

    [Fact]
    public void Order_ReturnsAllTags_InTheSameBlendedRankAsSelect()
    {
        var usage = new[] { U("a", 2, 100), U("b", 2, 400), U("c", 2, 300), U("d", 2, 200) };
        var ordered = TagQuickPicks.Order(usage);
        Assert.Equal(["b", "c", "d", "a"], ordered);              // even usage → recency, uncapped
        Assert.Equal(TagQuickPicks.Select(usage, 2).Tags, ordered.Take(2)); // Select is Order capped
    }

    [Fact]
    public void Order_Empty_ReturnsEmpty() => Assert.Empty(TagQuickPicks.Order([]));
}
