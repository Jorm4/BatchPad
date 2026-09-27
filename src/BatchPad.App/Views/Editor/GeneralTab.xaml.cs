using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BatchPad.App.ViewModels.Editor;

namespace BatchPad.App.Views.Editor;

public partial class GeneralTab : UserControl
{
    public GeneralTab()
    {
        InitializeComponent();
    }

    private void OnHotkeyBoxFocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        (DataContext as GeneralTabViewModel)?.RecordingHotkey(ReferenceEquals(e.NewFocus, sender));

    private void OnHotkeyBoxUnloaded(object sender, RoutedEventArgs e) =>
        (DataContext as GeneralTabViewModel)?.RecordingHotkey(false);
}
