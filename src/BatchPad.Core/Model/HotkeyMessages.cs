namespace BatchPad.Core.Model;

public static class HotkeyMessages
{
    public static string Invalid(string text) => $"'{text}' isn't a hotkey BatchPad can register.";

    public static string Duplicate(HotkeyGesture gesture, string owner) => $"{gesture} is already the hotkey of '{owner}'.";

    public static string Taken(HotkeyGesture gesture) => $"{gesture} is taken by another program.";
}
