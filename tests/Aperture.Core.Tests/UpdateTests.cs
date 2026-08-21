using Aperture.Core.Updates;

namespace Aperture.Core.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v0.8.0-beta1", "0.8.0-beta1", true)]
    [InlineData("0.8.0-beta1", "0.8.0-beta1", true)]
    [InlineData("V0.8.0", "0.8.0", false)]
    [InlineData("v0.8", "0.8.0", false)]
    [InlineData("0.8.0-beta1+abc1234", "0.8.0-beta1", true)]
    [InlineData("0.8.0+abc1234", "0.8.0", false)]
    public void Parse_Roundtrips(string input, string expected, bool prerelease)
    {
        var v = UpdateVersion.Parse(input);
        Assert.NotNull(v);
        Assert.Equal(expected, v!.ToString());
        Assert.Equal(prerelease, v.IsPrerelease);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v-beta1")]
    public void Parse_RejectsJunk(string? input) => Assert.Null(UpdateVersion.Parse(input));

    [Theory]
    [InlineData("0.8.0-beta1", "0.7.1-beta1", 1)]
    [InlineData("0.8.0", "0.8.0-beta1", 1)]
    [InlineData("0.8.0-beta2", "0.8.0-beta1", 1)]
    [InlineData("0.8.0-rc1", "0.8.0-beta9", 1)]
    [InlineData("0.8.0-beta1", "0.8.0-beta1", 0)]
    public void CompareTo_Orders(string a, string b, int sign)
    {
        var va = UpdateVersion.Parse(a)!;
        var vb = UpdateVersion.Parse(b)!;
        Assert.Equal(sign, Math.Sign(va.CompareTo(vb)));
        Assert.Equal(-sign, Math.Sign(vb.CompareTo(va)));
    }
}

public class UpdateOfferTests
{
    private static ReleaseInfo Rel(string tag, bool prerelease) =>
        new(tag, UpdateVersion.Parse(tag)!, prerelease, "", "Aperture-x.exe", "https://x/asset.exe", 1);

    private static UpdateVersion Cur(string v) => UpdateVersion.Parse(v)!;

    [Fact]
    public void SupersededPrerelease_IsNotOffered()
    {
        Assert.False(UpdateOffer.ShowPrerelease(
            Rel("v0.8.0-beta1", true), Rel("v0.8.0", false), Cur("0.8.0")));
    }

    [Fact]
    public void PrereleaseAheadOfBoth_IsOffered()
    {
        Assert.True(UpdateOffer.ShowPrerelease(
            Rel("v0.9.0-beta1", true), Rel("v0.8.0", false), Cur("0.8.0")));
    }

    [Fact]
    public void NextPrerelease_IsOfferedToSomeoneOnTheEarlierOne()
    {
        Assert.True(UpdateOffer.ShowPrerelease(
            Rel("v0.8.0-beta2", true), Rel("v0.7.1", false), Cur("0.8.0-beta1")));
    }

    [Fact]
    public void PrereleaseNewerThanStableButOlderThanRunning_IsNotOffered()
    {
        Assert.False(UpdateOffer.ShowPrerelease(
            Rel("v0.9.0-beta1", true), Rel("v0.8.0", false), Cur("0.9.0-beta2")));
    }

    [Fact]
    public void NoPrerelease_IsNotOffered()
        => Assert.False(UpdateOffer.ShowPrerelease(null, Rel("v0.8.0", false), Cur("0.8.0")));

    [Fact]
    public void PrereleaseWithNoStablePublishedYet_IsJudgedAgainstTheRunningVersion()
    {
        Assert.True(UpdateOffer.ShowPrerelease(Rel("v0.8.0-beta1", true), null, Cur("0.7.1-beta1")));
        Assert.False(UpdateOffer.ShowPrerelease(Rel("v0.7.1-beta1", true), null, Cur("0.8.0-beta1")));
    }

    [Fact]
    public void UnknownRunningVersion_StillRespectsSupersession()
    {
        Assert.False(UpdateOffer.ShowPrerelease(Rel("v0.8.0-beta1", true), Rel("v0.8.0", false), null));
        Assert.True(UpdateOffer.ShowPrerelease(Rel("v0.9.0-beta1", true), Rel("v0.8.0", false), null));
    }

    [Fact]
    public void NothingToCompareAgainst_OffersNoPrerelease()
        => Assert.False(UpdateOffer.ShowPrerelease(Rel("v0.1.0-alpha1", true), null, null));

    [Theory]
    [InlineData("v0.8.0", "0.7.1", true)]
    [InlineData("v0.8.0", "0.8.0", false)]
    [InlineData("v0.7.1", "0.8.0", false)]
    [InlineData("v0.8.0", "0.8.0-beta1", true)]
    public void StableUpdate_OfferedOnlyWhenNewer(string tag, string current, bool expected)
        => Assert.Equal(expected, UpdateOffer.ShowStableUpdate(Rel(tag, false), Cur(current)));

    [Fact]
    public void StableUpdate_NeedsAReleaseToOffer()
        => Assert.False(UpdateOffer.ShowStableUpdate(null, Cur("0.8.0-beta1")));

    [Fact]
    public void UnreadableRunningVersion_StillOffersTheStableRelease()
        => Assert.True(UpdateOffer.ShowStableUpdate(Rel("v0.8.0", false), null));

    [Theory]
    [InlineData("0.8.0-beta1", "0.8.0-beta1", "0.7.1-beta1", true)]
    [InlineData("0.8.0-beta2", "0.8.0-beta1", "0.7.1-beta1", true)]
    [InlineData("0.7.1-beta1", "0.8.0-beta1", "0.7.1-beta1", false)]
    [InlineData("0.8.0-beta1", "0.8.0-beta2", "0.7.1-beta1", true)]
    public void RestartIsNeededWhenTheDiskHasMovedPastUsOrCaughtUpWithTheOffer(
        string onDisk, string wanted, string running, bool restart)
        => Assert.Equal(restart,
            UpdateOffer.NeedsRestartNotUpdate(Cur(onDisk), Cur(wanted), Cur(running)));

    [Fact]
    public void UnreadableVersions_LetTheUpdateProceed()
    {
        Assert.False(UpdateOffer.NeedsRestartNotUpdate(null, Cur("0.8.0-beta1"), Cur("0.7.1-beta1")));
        Assert.False(UpdateOffer.NeedsRestartNotUpdate(null, null, null));
    }
}

public class ReleaseFeedTests
{
    private static string Release(string tag, bool prerelease, bool draft = false, bool withAsset = true, string? assetName = null)
    {
        var name = assetName ?? $"Aperture-{tag}-win-x64.exe";
        var assets = withAsset
            ? $@"[{{""name"":""{name}"",""browser_download_url"":""https://example.com/{tag}.exe"",""size"":100}}]"
            : "[]";
        return $@"{{""tag_name"":""{tag}"",""prerelease"":{prerelease.ToString().ToLowerInvariant()},
                  ""draft"":{draft.ToString().ToLowerInvariant()},""html_url"":""https://example.com/{tag}"",
                  ""assets"":{assets}}}";
    }

    private static string Feed(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void Select_PicksNewestOfEachChannel_ByVersionNotListOrder()
    {
        var json = Feed(
            Release("v0.7.1-beta1", prerelease: true),
            Release("v0.9.0-beta1", prerelease: true),
            Release("v0.8.0", prerelease: false),
            Release("v0.7.0", prerelease: false));
        var check = ReleaseFeed.Select(json);
        Assert.Equal("v0.8.0", check.Stable?.Tag);
        Assert.Equal("v0.9.0-beta1", check.PrereleaseRelease?.Tag);
    }

    [Fact]
    public void Select_IgnoresDraftsAndAssetlessReleases()
    {
        var json = Feed(
            Release("v0.9.0", prerelease: false, draft: true),
            Release("v0.8.0", prerelease: false, withAsset: false),
            Release("v0.7.0", prerelease: false));
        var check = ReleaseFeed.Select(json);
        Assert.Equal("v0.7.0", check.Stable?.Tag);
    }

    [Fact]
    public void Select_IgnoresMidgetAssetSuffix()
    {
        var json = Feed(
            Release("v0.9.0", prerelease: false, assetName: "Aperture-v0.9.0-win-x64-net10.exe"),
            Release("v0.8.0-beta1", prerelease: true));
        var check = ReleaseFeed.Select(json);
        Assert.Null(check.Stable);
        Assert.Equal("v0.8.0-beta1", check.PrereleaseRelease?.Tag);
        Assert.Equal("Aperture-v0.8.0-beta1-win-x64.exe", check.PrereleaseRelease?.AssetName);
    }

    [Fact]
    public void Select_EmptyFeed_YieldsNulls()
    {
        var check = ReleaseFeed.Select("[]");
        Assert.Null(check.Stable);
        Assert.Null(check.PrereleaseRelease);
    }

    [Fact]
    public void Select_CarriesAssetDetails()
    {
        var check = ReleaseFeed.Select(Feed(Release("v0.8.0-beta1", prerelease: true)));
        Assert.Equal("Aperture-v0.8.0-beta1-win-x64.exe", check.PrereleaseRelease?.AssetName);
        Assert.Equal(100, check.PrereleaseRelease?.AssetSize);
        Assert.StartsWith("https://example.com/", check.PrereleaseRelease?.AssetUrl);
    }

    [Fact]
    public void MislabelledPrerelease_IsNotTreatedAsStable()
    {
        // Aperture's published betas have shipped with prerelease:false on GitHub.
        // The version tail is the safety net.
        var check = ReleaseFeed.Select(Feed(Release("v0.8.0-beta1", prerelease: false)));
        Assert.Null(check.Stable);
        Assert.Equal("v0.8.0-beta1", check.PrereleaseRelease!.Tag);
        Assert.True(check.PrereleaseRelease.Prerelease);
    }

    [Fact]
    public void AgainstTheRealApertureFeedShape_CurrentBetaUsersCanUpdate()
    {
        var json = Feed(
            Release("v0.8.0-beta1", prerelease: false),
            Release("v0.7.1-beta1", prerelease: false),
            Release("v0.7.0-beta1", prerelease: false));
        var check = ReleaseFeed.Select(json);
        Assert.Null(check.Stable);
        Assert.Equal("v0.8.0-beta1", check.PrereleaseRelease!.Tag);

        Assert.False(UpdateOffer.ShowPrerelease(check.PrereleaseRelease, check.Stable, UpdateVersion.Parse("0.8.0-beta1")));
        Assert.True(UpdateOffer.ShowPrerelease(check.PrereleaseRelease, check.Stable, UpdateVersion.Parse("0.7.1-beta1")));
        Assert.False(UpdateOffer.ShowStableUpdate(check.Stable, UpdateVersion.Parse("0.8.0-beta1")));
    }
}
