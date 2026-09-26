using System.Windows;
using BatchPad.App.ViewModels.Parameters;

namespace BatchPad.App.Views;

public partial class AskDialog : Window
{
    public AskDialog()
    {
        InitializeComponent();
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (DataContext is ParameterFormViewModel { HasErrors: false })
            DialogResult = true;
    }
}
