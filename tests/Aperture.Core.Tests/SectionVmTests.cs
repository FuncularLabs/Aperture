using Aperture.App.ViewModels;

namespace Aperture.Core.Tests;

/// <summary>
/// A date section knows whether the user expanded or collapsed it: only those toggles go to other places and groupings
/// (round 24 of the multiple-libraries review).
/// </summary>
public class SectionVmTests
{
    private static SectionVm Section() => new() { Key = 2023, Label = "2023" };

    [Fact]
    public void HowABuildLeavesIt_IsntAToggle()
    {
        var section = Section();

        section.Start(expanded: false);

        Assert.False(section.IsExpanded);
        Assert.False(section.Toggled);
    }

    [Fact]
    public void TheUsersChange_IsAToggle()
    {
        var section = Section();
        section.Start(expanded: true);
        var changed = new List<string?>();
        section.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        section.IsExpanded = false; // the header's toggle, or the keyboard

        Assert.True(section.Toggled);
        Assert.Contains(nameof(SectionVm.IsExpanded), changed);
    }

    [Fact]
    public void SettingTheStateItHas_IsntAToggle()
    {
        var section = Section();
        section.Start(expanded: true);

        section.IsExpanded = true;

        Assert.False(section.Toggled);
    }

    [Fact]
    public void ABuildStartingASectionToggled_DoesntMakeItAToggle()
    {
        // A section a build starts as the user left it isn't their toggle in this view: the view model remembers those.
        var section = Section();
        section.IsExpanded = false;

        section.Start(expanded: false);

        Assert.False(section.Toggled);
        Assert.False(section.IsExpanded);
    }
}
