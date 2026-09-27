using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BatchPad.App.Views;

/// <summary>Ctrl+C and a Copy menu on an output list, copying the selected lines, or all shown, in log order.</summary>
public static class LogCopy
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(LogCopy), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>The <paramref name="selected"/> items' text, one per line, in the order of <paramref name="items"/>.</summary>
    public static string TextOf(IEnumerable<object> items, IEnumerable<object> selected)
    {
        var chosen = selected.ToHashSet();
        return string.Join(Environment.NewLine, items.Where(chosen.Contains));
    }

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ListBox list || !(bool)e.NewValue)
            return;
        list.SelectionMode = SelectionMode.Extended;
        list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, _) => ToClipboard(TextOf(list.Items.Cast<object>(), list.SelectedItems.Cast<object>())),
            (_, args) => args.CanExecute = list.SelectedItems.Count > 0));
        var copyAll = new MenuItem { Header = "Copy all" };
        copyAll.Click += (_, _) => ToClipboard(string.Join(Environment.NewLine, list.Items.Cast<object>()));
        list.ContextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = list, InputGestureText = "Ctrl+C" },
                copyAll,
                new MenuItem { Header = "Select all", Command = ApplicationCommands.SelectAll, CommandTarget = list, InputGestureText = "Ctrl+A" },
            },
        };
    }

    private static void ToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Another app holding the clipboard open; the user can copy again.
        }
    }
}
