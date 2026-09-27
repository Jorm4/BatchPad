using System.Windows.Controls;
using System.Windows.Input;
using BatchPad.Core.Model;

namespace BatchPad.App.Views;

/// <summary>Records the combination pressed while it has focus; Esc or Backspace clears it.</summary>
public sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
            return;
        e.Handled = true;
        if (modifiers == ModifierKeys.None && key is Key.Escape or Key.Back)
            Text = "";
        else if (GestureFor(key, modifiers) is { } gesture)
            Text = gesture.ToString();
    }

    public static HotkeyGesture? GestureFor(Key key, ModifierKeys modifiers) =>
        HotkeyGesture.From((HotkeyModifiers)(int)modifiers, KeyInterop.VirtualKeyFromKey(key));
}
