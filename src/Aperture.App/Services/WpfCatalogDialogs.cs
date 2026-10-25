using System.Windows;
using Microsoft.Win32;

namespace Aperture.App.Services;

/// <summary>The real dialogs behind <see cref="ICatalogDialogs"/>: the Windows folder picker, a text prompt, message boxes.</summary>
public sealed class WpfCatalogDialogs : ICatalogDialogs
{
    public string? PromptText(string title, string label, string initial) =>
        TextPromptDialog.Prompt(title, label, initial);

    public string? PickFolder(string title, string initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initialDirectory };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OKCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public void ShowMessage(string title, string message, bool isError) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK,
            isError ? MessageBoxImage.Error : MessageBoxImage.Information);
}
