using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Aperture.App;
using Aperture.Core.Indexing;
using Aperture.Core.Tests.Support;

namespace Aperture.Core.Tests;

/// <summary>
/// The online-only question's dialog itself (0.9.2 C1, C5; review gate 11): what it says, each button's answer, and that
/// Esc and the close box give none (the window takes that as Skip, for now only). Shown modally off screen.
/// </summary>
public class OnlineOnlyDialogTests
{
    // Shows the dialog modally, does what's asked once it's up, and hands back the dialog once it has closed.
    private static OnlineOnlyDialog Show(string folder, int count, Action<OnlineOnlyDialog> act)
    {
        OnlineOnlyDialog? shown = null;
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            // Numbers are written in the user's format; these tests read them in en-US's (this thread's own culture).
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            var dialog = new OnlineOnlyDialog(folder, count)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false,
            };
            dialog.ContentRendered += (_, _) => dialog.Dispatcher.BeginInvoke(() => act(dialog), DispatcherPriority.Background);
            dialog.ShowDialog();
            shown = dialog;
        });
        return shown!;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Theory]
    [InlineData("SkipButton", OnlineOnlyChoice.Skip)]
    [InlineData("BackgroundButton", OnlineOnlyChoice.Background)]
    [InlineData("NowButton", OnlineOnlyChoice.Now)]
    public void EachButton_GivesItsOwnAnswer_NotKept(string name, OnlineOnlyChoice choice)
    {
        var dialog = Show("Photos", 3, d => Click((Button)d.FindName(name)));

        Assert.Equal(new OnlineOnlyAnswer(choice, Remember: false), dialog.Answer);
    }

    [Fact]
    public void Remember_StartsUnticked_AndKeepsTheAnswerWhenTicked()
    {
        var dialog = Show("Photos", 3, d =>
        {
            var remember = (CheckBox)d.FindName("RememberCheck");
            Assert.False(remember.IsChecked);
            remember.IsChecked = true;
            Click((Button)d.FindName("BackgroundButton"));
        });

        Assert.Equal(new OnlineOnlyAnswer(OnlineOnlyChoice.Background, Remember: true), dialog.Answer);
    }

    [Fact]
    public void Esc_GivesNoAnswer_EvenWithRememberTicked()
    {
        var dialog = Show("Photos", 3, d =>
        {
            ((CheckBox)d.FindName("RememberCheck")).IsChecked = true;
            var esc = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(d)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            d.RaiseEvent(esc);
        });

        Assert.Null(dialog.Answer);
    }

    [Fact]
    public void TheCloseBox_GivesNoAnswer()
    {
        var dialog = Show("Photos", 3, d => d.Close());

        Assert.Null(dialog.Answer);
    }

    [Fact]
    public void Enter_IsSkip_AndNoButtonIsCancel()
    {
        // No button is Cancel: one that was would answer for Esc (and with Remember, if ticked).
        bool? skipIsDefault = null;
        var cancels = -1;
        Show("Photos", 3, d =>
        {
            skipIsDefault = ((Button)d.FindName("SkipButton")).IsDefault;
            cancels = new[] { "SkipButton", "BackgroundButton", "NowButton" }.Count(n => ((Button)d.FindName(n)).IsCancel);
            d.Close();
        });

        Assert.True(skipIsDefault);
        Assert.Equal(0, cancels);
    }

    [Theory]
    [InlineData(1, "“Trips” has 1 online-only file")]
    [InlineData(2, "“Trips” has 2 online-only files")]
    [InlineData(1234, "“Trips” has 1,234 online-only files")]
    public void ItNamesTheFolder_AndHowMany(int count, string headline)
    {
        string? text = null;
        string? body = null;
        Show("Trips", count, d =>
        {
            text = ((TextBlock)d.FindName("HeadlineText")).Text;
            body = ((TextBlock)d.FindName("BodyText")).Text;
            d.Close();
        });

        Assert.Equal(headline, text);
        Assert.Contains("downloads each one", body);
    }
}
