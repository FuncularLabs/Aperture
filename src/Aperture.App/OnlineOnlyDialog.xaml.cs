using System.Windows;
using System.Windows.Input;
using Aperture.Core.Indexing;

namespace Aperture.App;

/// <summary>The answer to the online-only question: what to do, and whether to keep it for the folder.</summary>
public readonly record struct OnlineOnlyAnswer(OnlineOnlyChoice Choice, bool Remember);

/// <summary>
/// Asks what to do with the online-only files an index of a folder would download (0.9.2 C1): skip them, index them
/// after the rest, or now, and whether to remember it for the folder (unticked). Esc and the close box give no answer
/// (<see cref="Answer"/> stays <c>null</c>): the window takes that as Skip, for now only.
/// </summary>
public partial class OnlineOnlyDialog : Window
{
    public OnlineOnlyDialog(string folderName, int count)
    {
        InitializeComponent();
        HeadlineText.Text = count == 1
            ? $"“{folderName}” has 1 online-only file"
            : $"“{folderName}” has {count:n0} online-only files";
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>What the user chose; <c>null</c> when they closed the dialog without choosing.</summary>
    public OnlineOnlyAnswer? Answer { get; private set; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close(); // no answer
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Choose(OnlineOnlyChoice.Skip);

    private void OnBackground(object sender, RoutedEventArgs e) => Choose(OnlineOnlyChoice.Background);

    private void OnNow(object sender, RoutedEventArgs e) => Choose(OnlineOnlyChoice.Now);

    private void Choose(OnlineOnlyChoice choice)
    {
        Answer = new OnlineOnlyAnswer(choice, RememberCheck.IsChecked == true);
        DialogResult = true;
    }
}
