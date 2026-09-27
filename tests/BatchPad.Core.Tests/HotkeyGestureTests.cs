using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class HotkeyGestureTests
{
    [TestMethod]
    [DataRow("Ctrl+Shift+B", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x42)]
    [DataRow("Alt+F5", HotkeyModifiers.Alt, 0x74)]
    [DataRow("F13", HotkeyModifiers.None, 0x7C)]
    [DataRow("shift + ctrl + 7", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x37)]
    [DataRow("Ctrl+Win+PageUp", HotkeyModifiers.Ctrl | HotkeyModifiers.Win, 0x21)]
    public void ParsesModifiersAndKey(string text, HotkeyModifiers modifiers, int virtualKey)
    {
        Assert.IsTrue(HotkeyGesture.TryParse(text, out var gesture));
        Assert.AreEqual(new HotkeyGesture(modifiers, virtualKey), gesture);
    }

    [TestMethod]
    [DataRow("B")]
    [DataRow("Ctrl+")]
    [DataRow("Win+L")]
    [DataRow("Ctrl+Ctrl+X")]
    [DataRow("Shift+A")]
    [DataRow("Ctrl+Hyper+X")]
    [DataRow("")]
    public void RejectsWhatItWontRegister(string text) => Assert.IsFalse(HotkeyGesture.TryParse(text, out _));

    [TestMethod]
    public void PrintsInCanonicalOrder()
    {
        Assert.IsTrue(HotkeyGesture.TryParse("shift+alt+ctrl+esc", out var gesture));
        Assert.AreEqual("Ctrl+Alt+Shift+Escape", gesture.ToString());
    }

    [TestMethod]
    public void AHotkeyUsedInTheWorkspaceAndMyScriptsIsALoadProblem()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("batchpad.json"), """
            { "id": "ws", "scripts": [ { "id": "build", "name": "Build", "path": "a.bat", "hotkey": "Ctrl+Shift+B" } ] }
            """);
        var paths = new AppPaths(dir.Path("data"));
        Directory.CreateDirectory(Path.GetDirectoryName(paths.UserFile("ws"))!);
        File.WriteAllText(paths.UserFile("ws"), """
            { "scripts": [ { "id": "mine", "name": "Mine", "command": "echo", "hotkey": "shift+ctrl+b" },
                           { "id": "odd", "name": "Odd", "command": "echo", "hotkey": "Q" } ] }
            """);

        var loaded = WorkspaceLoader.Load(dir.Path("batchpad.json"), paths);

        CollectionAssert.AreEquivalent(new[]
        {
            "'Odd': 'Q' isn't a hotkey BatchPad can register.",
            "'Build': Ctrl+Shift+B is already the hotkey of 'Mine'.",
        }, loaded.Errors.Select(e => e.Message).ToArray());
    }
}
