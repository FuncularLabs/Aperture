using System.Windows;
using System.Windows.Input;
using Reel.App.ViewModels;

namespace Reel.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>"/" focuses the search box (unless the user is already typing in a text field).</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Oem2 && Keyboard.Modifiers == ModifierKeys.None
            && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnItemsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.OpenSelectedCommand.CanExecute(null) == true)
            ViewModel.OpenSelectedCommand.Execute(null);
    }

    private void OnItemsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        e.Handled = true;
        if (e.Delta > 0)
            ViewModel?.ZoomIn();
        else
            ViewModel?.ZoomOut();
    }
}
