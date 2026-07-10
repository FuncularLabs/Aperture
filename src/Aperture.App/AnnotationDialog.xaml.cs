using System.Windows;

namespace Aperture.App;

public partial class AnnotationDialog : Window
{
    public AnnotationDialog()
    {
        InitializeComponent();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
