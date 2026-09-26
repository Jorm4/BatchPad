using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BatchPad.App.ViewModels;

namespace BatchPad.App.Views;

public partial class CommandPalette : UserControl
{
    public CommandPalette()
    {
        InitializeComponent();
    }

    private CommandPaletteViewModel? Palette => DataContext as CommandPaletteViewModel;

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            Dispatcher.BeginInvoke(() => Keyboard.Focus(QueryBox));
    }

    private void OnFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsKeyboardFocusWithin && Palette is { IsOpen: true } palette)
            palette.IsOpen = false;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => Palette?.RunCommand.Execute(null);
}
