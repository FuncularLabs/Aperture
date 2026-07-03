using System.Windows;
using System.Windows.Input;
using Reel.App.ViewModels;

namespace Reel.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

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
