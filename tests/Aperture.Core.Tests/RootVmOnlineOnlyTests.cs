using Aperture.App.ViewModels;
using Aperture.Core.Indexing;
using Aperture.Core.Models;

namespace Aperture.Core.Tests;

/// <summary>A folder's Online-only files menu, on its view model (0.9.2 C6): the four answers, and its checks.</summary>
public class RootVmOnlineOnlyTests
{
    private static (RootVm Root, List<OnlineOnlyChoice?> Chosen) Make()
    {
        var chosen = new List<OnlineOnlyChoice?>();
        var root = new RootVm(new Root { Id = 1, Path = @"C:\Photos", Alias = "Photos" },
            (_, _) => { }, (_, _) => { }, (_, _) => { }, (_, c) => chosen.Add(c));
        return (root, chosen);
    }

    [Fact]
    public void TheMenusAnswers_AreSaid()
    {
        var (root, chosen) = Make();

        foreach (var answer in new[] { "Ask", "Skip", "Background", "Now" })
            root.SetOnlineOnlyCommand.Execute(answer);

        Assert.Equal([null, OnlineOnlyChoice.Skip, OnlineOnlyChoice.Background, OnlineOnlyChoice.Now], chosen);
    }

    [Theory]
    [InlineData("1")] // a number would parse as an answer
    [InlineData("Sometimes")]
    [InlineData("")]
    public void AnythingElse_DoesNothing(string answer)
    {
        var (root, chosen) = Make();

        root.SetOnlineOnlyCommand.Execute(answer);

        Assert.Empty(chosen);
    }

    [Fact]
    public void TheChecks_FollowTheAnswerInForce()
    {
        var (root, _) = Make();
        var changed = new List<string?>();
        root.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal((true, false, false, false), Checks(root));
        root.OnlineOnly = OnlineOnlyChoice.Skip;
        Assert.Equal((false, true, false, false), Checks(root));
        root.OnlineOnly = OnlineOnlyChoice.Background;
        Assert.Equal((false, false, true, false), Checks(root));
        root.OnlineOnly = OnlineOnlyChoice.Now;
        Assert.Equal((false, false, false, true), Checks(root));
        root.OnlineOnly = null;
        Assert.Equal((true, false, false, false), Checks(root));
        foreach (var name in new[] { nameof(RootVm.OnlineOnly), nameof(RootVm.OnlineOnlyAsks), nameof(RootVm.OnlineOnlySkips),
                     nameof(RootVm.OnlineOnlyInBackground), nameof(RootVm.OnlineOnlyNow) })
            Assert.Contains(name, changed);

        static (bool, bool, bool, bool) Checks(RootVm r) =>
            (r.OnlineOnlyAsks, r.OnlineOnlySkips, r.OnlineOnlyInBackground, r.OnlineOnlyNow);
    }

    [Fact]
    public void WithoutAHandler_TheMenuDoesNothing()
    {
        var root = new RootVm(new Root { Id = 1, Path = @"C:\Photos", Alias = "Photos" }, (_, _) => { }, (_, _) => { }, (_, _) => { });

        root.SetOnlineOnlyCommand.Execute("Now"); // no throw

        Assert.Null(root.OnlineOnly);
    }
}
