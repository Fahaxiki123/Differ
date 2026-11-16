using System.Windows;

namespace Differ.UI.Gallery;

public partial class SampleDialog : Window
{
    public SampleDialog()
    {
        InitializeComponent();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
