using System.IO;
using System.Windows;

namespace Aperture.App;

/// <summary>
/// Confirms a picked folder and asks whether to index its subfolders. Shown after the OS folder
/// picker, which can't host extra controls of its own.
/// </summary>
public partial class AddFolderDialog : Window
{
    public AddFolderDialog(string folderPath)
    {
        InitializeComponent();
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
        FolderNameText.Text = string.IsNullOrEmpty(name) ? folderPath : name;
        FolderPathText.Text = folderPath;
    }

    /// <summary>Whether the root should index its whole tree (checked) or just its own folder.</summary>
    public bool IncludeSubfolders => SubfoldersCheck.IsChecked == true;

    private void OnAdd(object sender, RoutedEventArgs e) => DialogResult = true;
}
