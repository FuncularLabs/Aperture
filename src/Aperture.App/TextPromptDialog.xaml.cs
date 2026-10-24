using System.Windows;

namespace Aperture.App;

/// <summary>A small modal text-input prompt (WPF has no built-in input box). Used for renaming a library.</summary>
public partial class TextPromptDialog : Window
{
    private TextPromptDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Input.SelectAll();
            Input.Focus();
        };
    }

    /// <summary>Shows the prompt and returns the entered text, or <c>null</c> if cancelled.</summary>
    public static string? Prompt(string title, string label, string initial = "")
    {
        var dialog = new TextPromptDialog
        {
            Title = title,
            Owner = Application.Current.MainWindow,
        };
        dialog.PromptLabel.Text = label;
        dialog.Input.Text = initial;
        return dialog.ShowDialog() == true ? dialog.Input.Text : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
