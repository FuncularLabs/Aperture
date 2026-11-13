using Aperture.Core.Indexing;
using Aperture.Core.Models;

namespace Aperture.Core.Tests;

/// <summary>
/// The runs in progress, for the window's indexing lines (0.9.1 batch, item 4). Kept by run, not by folder: a folder can
/// be indexed twice at once (its watcher fires during a run), and the first run's end mustn't silence the second
/// (review R1-8).
/// </summary>
public class IndexRunsTests : IDisposable
{
    private readonly System.Globalization.CultureInfo _culture = System.Globalization.CultureInfo.CurrentCulture;

    public IndexRunsTests() =>
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");

    public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _culture;

    private static IndexProgress P(int processed, int total, int onlineOnly = 0) =>
        new() { RootAlias = "x", Phase = IndexPhase.Indexing, Processed = processed, Total = total, OnlineOnly = onlineOnly };

    private static string Alias(long rootId) => rootId switch { 1 => "Photos", 2 => "Cloud", _ => "?" };

    [Fact]
    public void NoRuns_NoLines()
    {
        var runs = new IndexRuns();

        Assert.Equal(0, runs.Count);
        Assert.Equal(("", ""), runs.Lines(Alias));
    }

    [Fact]
    public void ARun_SaysItsLatestReport()
    {
        var runs = new IndexRuns();
        var run = runs.Start(1);
        Assert.Equal("Indexing Photos…", runs.Lines(Alias).Headline);

        runs.Report(run, P(3, 9, onlineOnly: 2));

        Assert.Equal("Indexing Photos: 3 of 9 files", runs.Lines(Alias).Headline);
        Assert.EndsWith("2 of the files are online-only, so each one is downloaded as it's indexed.", runs.Lines(Alias).Note);
        runs.End(run);
        Assert.Equal(0, runs.Count);
    }

    [Fact]
    public void TwoRunsOfOneFolder_AreOneFolder_AndTheSecondGoesOnAfterTheFirstEnds()
    {
        var runs = new IndexRuns();
        var first = runs.Start(1);
        var second = runs.Start(1);
        runs.Report(first, P(5, 10));
        runs.Report(second, P(1, 10));

        Assert.Equal("Indexing Photos: 1 of 10 files", runs.Lines(Alias).Headline); // one folder: its newest run

        runs.End(first);
        runs.Report(second, P(7, 10));
        Assert.Equal(1, runs.Count);
        Assert.Equal("Indexing Photos: 7 of 10 files", runs.Lines(Alias).Headline);
    }

    [Fact]
    public void SeveralFolders_SayHowMany_AndAddUpTheirOnlineOnlyFiles()
    {
        var runs = new IndexRuns();
        runs.Report(runs.Start(1), P(3, 9, onlineOnly: 2));
        runs.Report(runs.Start(2), P(1, 5, onlineOnly: 3));

        var (headline, note) = runs.Lines(Alias);

        Assert.Equal("Indexing 2 folders…", headline);
        Assert.EndsWith("5 of the files are online-only, so each one is downloaded as it's indexed.", note);
    }

    [Fact]
    public void AReportForARunThatHasEnded_IsDropped()
    {
        var runs = new IndexRuns();
        var run = runs.Start(1);
        runs.End(run);

        runs.Report(run, P(1, 2)); // posted before its end, handled after
        runs.End(run);             // and an end twice is harmless

        Assert.Equal(0, runs.Count);
        Assert.Equal(("", ""), runs.Lines(Alias));
    }
}
