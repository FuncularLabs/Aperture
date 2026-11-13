using Aperture.Core.Formatting;
using Aperture.Core.Models;

namespace Aperture.Core.Tests;

/// <summary>
/// What the window says while folders are indexed (0.9.1 batch, item 4: count first, then a progress bar "with friendly
/// messaging, such as 'This might take a while'"). The status bar and the indexing card show these lines; the folder
/// tree shows the short status beside the folder.
/// </summary>
public class IndexingMessageTests : IDisposable
{
    // Numbers are written in the user's format; these tests read them in en-US's, whatever the machine's is.
    private readonly System.Globalization.CultureInfo _culture = System.Globalization.CultureInfo.CurrentCulture;

    public IndexingMessageTests() =>
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");

    public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _culture;

    [Fact]
    public void Numbers_AreWrittenInTheUsersFormat()
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("Indexing Photos: 1.234 of 12.345 files",
            IndexingMessage.Headline("Photos", P(IndexPhase.Indexing, 1234, 12345), 1));
    }

    private static IndexProgress P(IndexPhase phase, int processed = 0, int total = 0, int onlineOnly = 0) =>
        new() { RootAlias = "Photos", Phase = phase, Processed = processed, Total = total, OnlineOnly = onlineOnly };

    [Fact]
    public void RootStatus_SaysWhatTheRunIsDoing()
    {
        Assert.Equal("counting…", IndexingMessage.RootStatus(P(IndexPhase.Scanning)));
        Assert.Equal("counting… 12,345", IndexingMessage.RootStatus(P(IndexPhase.Scanning, 12345)));
        Assert.Equal("indexing 200 / 230", IndexingMessage.RootStatus(P(IndexPhase.Indexing, 200, 230))); // as before
        Assert.Equal("cleaning up…", IndexingMessage.RootStatus(P(IndexPhase.Pruning)));
        Assert.Equal("", IndexingMessage.RootStatus(P(IndexPhase.Done, 230, 230)));
    }

    [Fact]
    public void TheHeadline_ForOneFolder_CountsThenIndexes()
    {
        Assert.Equal("Counting files in Photos…", IndexingMessage.Headline("Photos", P(IndexPhase.Scanning), 1));
        Assert.Equal("Counting files in Photos… 12,345 so far",
            IndexingMessage.Headline("Photos", P(IndexPhase.Scanning, 12345), 1));
        Assert.Equal("Indexing Photos: 1,234 of 12,345 files",
            IndexingMessage.Headline("Photos", P(IndexPhase.Indexing, 1234, 12345), 1));
        Assert.Equal("Indexing Photos: 1 of 1 file", IndexingMessage.Headline("Photos", P(IndexPhase.Indexing, 1, 1), 1));
        Assert.Equal("Photos: cleaning up…", IndexingMessage.Headline("Photos", P(IndexPhase.Pruning), 1));
        Assert.Equal("Indexing Photos…", IndexingMessage.Headline("Photos", null, 1));               // no report yet
        Assert.Equal("Indexing Photos…", IndexingMessage.Headline("Photos", P(IndexPhase.Done, 3, 3), 1));
    }

    [Fact]
    public void TheHeadline_ForSeveralFolders_SaysHowMany()
    {
        Assert.Equal("Indexing 2 folders…", IndexingMessage.Headline("Photos", P(IndexPhase.Indexing, 1, 9), 2));
        Assert.Equal("Indexing 3 folders…", IndexingMessage.Headline(null, null, 3));
    }

    [Fact]
    public void TheHeadline_ForNoFolder_IsEmpty() => Assert.Equal("", IndexingMessage.Headline(null, null, 0));

    [Fact]
    public void For_TheRunsInProgress()
    {
        Assert.Equal(("", ""), IndexingMessage.For([]));
        Assert.Equal(("Indexing Photos: 3 of 9 files", "This might take a while. You can keep browsing."),
            IndexingMessage.For([("Photos", P(IndexPhase.Indexing, 3, 9))]));
        Assert.Equal(("Indexing Photos…", "This might take a while. You can keep browsing."),
            IndexingMessage.For([("Photos", null)]));
        var (headline, note) = IndexingMessage.For(
            [("Photos", P(IndexPhase.Indexing, 3, 9, onlineOnly: 2)), ("Cloud", P(IndexPhase.Indexing, 1, 5, onlineOnly: 3)), ("New", null)]);
        Assert.Equal("Indexing 3 folders…", headline);
        Assert.EndsWith("5 of the files are online-only, so each one is downloaded as it's indexed.", note); // in all
    }

    [Fact]
    public void TheFriendlyLine_IsShownWhileIndexing()
    {
        Assert.Equal("This might take a while. You can keep browsing.", IndexingMessage.Note(0, indexing: true));
        Assert.Equal("", IndexingMessage.Note(0, indexing: false));
        Assert.Equal("", IndexingMessage.Note(25, indexing: false));
    }

    [Fact]
    public void TheOnlineOnlyLine_OnlyWhenThereAreSome()
    {
        Assert.Equal(
            "This might take a while. You can keep browsing. 1,234 of the files are online-only, so each one is "
            + "downloaded as it's indexed.",
            IndexingMessage.Note(1234, indexing: true));
        Assert.Equal(
            "This might take a while. You can keep browsing. 1 of the files is online-only, so it's downloaded as "
            + "it's indexed.",
            IndexingMessage.Note(1, indexing: true));
    }
}
