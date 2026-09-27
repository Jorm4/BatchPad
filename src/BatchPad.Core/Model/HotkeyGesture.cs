using System.Diagnostics.CodeAnalysis;

namespace BatchPad.Core.Model;

/// <summary>Values match Win32's <c>MOD_*</c> flags.</summary>
[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

/// <summary>A global hotkey such as <c>Ctrl+Shift+B</c>: modifiers and a Win32 virtual-key code.</summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    private static readonly Dictionary<string, int> NamedKeys = BuildNamedKeys();
    private static readonly Dictionary<int, string> KeyNames = NamedKeys
        .GroupBy(k => k.Value).ToDictionary(g => g.Key, g => g.First().Key);

    private static readonly Dictionary<string, HotkeyModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = HotkeyModifiers.Ctrl,
        ["Control"] = HotkeyModifiers.Ctrl,
        ["Alt"] = HotkeyModifiers.Alt,
        ["Shift"] = HotkeyModifiers.Shift,
        ["Win"] = HotkeyModifiers.Win,
        ["Windows"] = HotkeyModifiers.Win,
    };

    private const int F1 = 0x70, F13 = 0x7C, F24 = 0x87;

    public string KeyName => KeyNames[VirtualKey];

    public override string ToString()
    {
        var parts = new List<string>();
        foreach (var modifier in (HotkeyModifiers[])[HotkeyModifiers.Ctrl, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Win])
            if (Modifiers.HasFlag(modifier))
                parts.Add(modifier.ToString());
        parts.Add(KeyName);
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Split('+').Select(p => p.Trim()).ToArray();
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            if (!ModifierNames.TryGetValue(part, out var modifier) || modifiers.HasFlag(modifier))
                return false;
            modifiers |= modifier;
        }
        if (!NamedKeys.TryGetValue(parts[^1], out var key))
            return false;
        gesture = From(modifiers, key);
        return gesture is not null;
    }

    /// <summary>Null for a key BatchPad doesn't name, or a combination it won't register.</summary>
    public static HotkeyGesture? From(HotkeyModifiers modifiers, int virtualKey) =>
        KeyNames.ContainsKey(virtualKey) && IsAllowed(modifiers, virtualKey) ? new HotkeyGesture(modifiers, virtualKey) : null;

    // Win+key alone is reserved by Windows, and Shift+letter would swallow capitals everywhere.
    private static bool IsAllowed(HotkeyModifiers modifiers, int virtualKey) => modifiers switch
    {
        HotkeyModifiers.None => virtualKey is >= F13 and <= F24,
        HotkeyModifiers.Win => false,
        HotkeyModifiers.Shift => virtualKey is >= F1 and <= F24,
        _ => true,
    };

    private static Dictionary<string, int> BuildNamedKeys()
    {
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 'A'; c <= 'Z'; c++)
            keys[c.ToString()] = c;
        for (var d = '0'; d <= '9'; d++)
            keys[d.ToString()] = d;
        for (var n = 1; n <= 24; n++)
            keys[$"F{n}"] = F1 + n - 1;
        for (var n = 0; n <= 9; n++)
            keys[$"NumPad{n}"] = 0x60 + n;
        (string Name, int Key)[] named =
        [
            ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Escape", 0x1B), ("Esc", 0x1B), ("Backspace", 0x08),
            ("Insert", 0x2D), ("Delete", 0x2E), ("Del", 0x2E), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
            ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28), ("Pause", 0x13), ("PrintScreen", 0x2C),
            ("Plus", 0xBB), ("Minus", 0xBD), ("Comma", 0xBC), ("Period", 0xBE),
        ];
        foreach (var (name, key) in named)
            keys[name] = key;
        return keys;
    }
}
