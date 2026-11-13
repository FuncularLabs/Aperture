using Aperture.Core.Indexing;
using Aperture.Core.Settings;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>The answers to the online-only question, per folder: the session's and the kept ones (0.9.2 C5–C6).</summary>
public class OnlineOnlyChoicesTests
{
    [Fact]
    public void NothingIsKnown_AtFirst()
    {
        using var dir = new TempDir();
        var choices = new OnlineOnlyChoices(new SettingsService(dir.Path));

        Assert.Null(choices.Known(1));
        Assert.Null(choices.Kept(1));
    }

    [Fact]
    public void AnAnswer_HoldsForTheSession_AndIsntKept()
    {
        using var dir = new TempDir();
        var choices = new OnlineOnlyChoices(new SettingsService(dir.Path));

        choices.Answer(1, OnlineOnlyChoice.Skip, keep: false);

        Assert.Equal(OnlineOnlyChoice.Skip, choices.Known(1));
        Assert.Null(choices.Kept(1));
        Assert.Null(choices.Known(2)); // another folder is its own
        Assert.Null(new OnlineOnlyChoices(new SettingsService(dir.Path)).Known(1)); // a restart asks again
    }

    [Fact]
    public void AKeptAnswer_HoldsAfterARestart()
    {
        using var dir = new TempDir();
        new OnlineOnlyChoices(new SettingsService(dir.Path)).Answer(7, OnlineOnlyChoice.Background, keep: true);

        var after = new OnlineOnlyChoices(new SettingsService(dir.Path));

        Assert.Equal(OnlineOnlyChoice.Background, after.Known(7));
        Assert.Equal(OnlineOnlyChoice.Background, after.Kept(7));
    }

    [Fact]
    public void AKeptAnswer_WinsOverTheSessions()
    {
        using var dir = new TempDir();
        var choices = new OnlineOnlyChoices(new SettingsService(dir.Path));
        choices.Answer(1, OnlineOnlyChoice.Now, keep: true);

        choices.Answer(1, OnlineOnlyChoice.Skip, keep: false);

        Assert.Equal(OnlineOnlyChoice.Now, choices.Known(1));
    }

    [Fact]
    public void Forget_ForgetsBoth()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        var choices = new OnlineOnlyChoices(settings);
        choices.Answer(1, OnlineOnlyChoice.Now, keep: true);
        choices.Answer(2, OnlineOnlyChoice.Skip, keep: true);

        choices.Forget(1);
        choices.Forget(3); // nothing to forget: harmless

        Assert.Null(choices.Known(1));
        Assert.Null(new OnlineOnlyChoices(new SettingsService(dir.Path)).Known(1));
        Assert.Equal(OnlineOnlyChoice.Skip, new OnlineOnlyChoices(new SettingsService(dir.Path)).Known(2));
    }

    [Theory]
    [InlineData("\"7\"")]          // a number isn't an answer
    [InlineData("\"Sometimes\"")]
    [InlineData("\"\"")]
    public void WhatIsntAnAnswer_IsAsIfNone(string value)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, SettingsService.FileName),
            "{ \"GroupByDate\": false, \"OnlineOnlyChoices\": { \"1\": " + value + ", \"2\": \"skip\" } }");

        var choices = new OnlineOnlyChoices(new SettingsService(dir.Path));

        Assert.Null(choices.Known(1));
        Assert.Equal(OnlineOnlyChoice.Skip, choices.Known(2)); // the names, in any case
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"Skip\"")]
    [InlineData("{ \"1\": 3, \"2\": [\"Now\"], \"3\": \"Now\" }")]
    [InlineData("null")]
    public void AMalformedValue_NeverCostsTheOtherSettings(string value)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, SettingsService.FileName),
            "{ \"GroupByDate\": false, \"OnlineOnlyChoices\": " + value + " }");

        var settings = new SettingsService(dir.Path);

        Assert.False(settings.Current.GroupByDate);
        var choices = new OnlineOnlyChoices(settings);
        Assert.Null(choices.Known(1));
        Assert.Equal(value.Contains("\"3\": \"Now\"") ? OnlineOnlyChoice.Now : null, choices.Known(3));
    }
}
