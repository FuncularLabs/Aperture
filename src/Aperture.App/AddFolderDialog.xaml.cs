using System.IO;
using System.Windows;

namespace Aperture.App;

/// <summary>
/// Confirms the folders being added and asks whether to index their subfolders (Yes unless unticked). Shown after
/// the OS folder picker, which can't host extra controls of its own, and for folders dropped on the window.
/// </summary>
public partial class AddFolderDialog : Window
{
    public AddFolderDialog(IReadOnlyList<string> folderPaths)
    {
        InitializeComponent();
        if (folderPaths.Count == 1)
        {
            FolderNameText.Text = NameOf(folderPaths[0]);
            FolderPathText.Text = folderPaths[0];
        }
        else
        {
            FolderNameText.Text = $"{folderPaths.Count} folders";
            FolderPathText.Text = string.Join(", ", folderPaths.Select(NameOf));
        }
    }

    /// <summary>Whether each folder should index its whole tree (checked) or just its own folder.</summary>
    public bool IncludeSubfolders => SubfoldersCheck.IsChecked == true;

    private static string NameOf(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? folderPath : name;
    }

    private void OnAdd(object sender, RoutedEventArgs e) => DialogResult = true;
}
