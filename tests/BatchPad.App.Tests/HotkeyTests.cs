using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Editor;
using BatchPad.App.Views;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class HotkeyTests
{
    private const string HelloBat = """{ "id": "hello-bat", "name": "hello.bat", "path": "hello.bat" }""";
    private const string HelloPy = """{ "id": "hello-py", "name": "hello.py", "path": "hello.py" }""";
    private const string HelloPs1 = """{ "id": "hello-ps1", "name": "hello.ps1", "path": "hello.ps1" }""";

    [TestMethod]
    public void RegistersOnLoadAndAPressRunsTheNode()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var (main, api) = Open(test, "Ctrl+Shift+B", trusted: true, launcher);

        var (id, gesture) = api.Registered.Single();
        Assert.AreEqual("Ctrl+Shift+B", gesture.ToString());
        api.Press(id);

        Assert.AreEqual("hello.bat", launcher.Requests.Single().Script.Name);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
    }

    [TestMethod]
    public void AReloadWithAChangedKeyReregistersAndARemovedKeyIsUnregistered()
    {
        using var test = new TestWorkspace();
        var (main, api) = Open(test, "Ctrl+Shift+B", trusted: true);

        SetHotkey(main, "Alt+F5");
        main.Reload();
        Assert.AreEqual("Alt+F5", api.Registered.Values.Single().ToString());

        SetHotkey(main, null);
        main.Reload();
        Assert.IsEmpty(api.Registered);
    }

    [TestMethod]
    public void AnUntrustedWorkspaceRegistersNoHotkeysUntilTrusted()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        WriteMyScript(test, "Ctrl+Alt+M");
        WriteHotkey(demo, "Ctrl+Shift+B");
        var main = test.OpenMain(demo, launcher: new FakeLauncher(), shell: new FakeShell());
        var api = new FakeHotkeyApi();

        main.Hotkeys = new HotkeyService(api, main.RunByKey);
        Assert.IsEmpty(api.Registered);

        main.TrustWorkspaceCommand.Execute(null);
        CollectionAssert.AreEquivalent(new[] { "Ctrl+Alt+M", "Ctrl+Shift+B" }, api.Registered.Values.Select(g => g.ToString()).ToList());
    }

    [TestMethod]
    public void AReloadThatMovesAKeyToAnotherNodeRunsTheNewNode()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var (main, api) = Open(test, "Ctrl+Shift+B", trusted: true, launcher);

        SetHotkey(main, null);
        var file = main.Workspace!.FilePath;
        File.WriteAllText(file, File.ReadAllText(file).Replace(HelloPy, HelloPy.Replace(" }", ", \"hotkey\": \"Ctrl+Shift+B\" }")));
        main.Reload();

        var (id, gesture) = api.Registered.Single();
        Assert.AreEqual("Ctrl+Shift+B", gesture.ToString());
        api.Press(id);
        Assert.AreEqual("hello.py", launcher.Requests.Single().Script.Name);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
    }

    [TestMethod]
    public void IsTakenOnAKeyBatchPadHoldsIsFalseWithoutTryingIt()
    {
        using var test = new TestWorkspace();
        var (main, api) = Open(test, "Ctrl+Shift+B", trusted: true);
        var calls = api.RegisterCalls;

        Assert.IsFalse(main.Hotkeys!.IsTaken(Gesture("Ctrl+Shift+B")));
        Assert.AreEqual(calls, api.RegisterCalls);
    }

    [TestMethod]
    public void FocusingTheHotkeyBoxSuspendsBatchPadsKeysUntilItLosesFocus()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var api = new FakeHotkeyApi { Held = { "Ctrl+Alt+H" } };
        var (main, _) = Open(test, "Ctrl+Shift+B", trusted: true, launcher, api);
        var editor = EditHello(main);

        editor.General.RecordingHotkey(true);
        Assert.IsEmpty(api.Registered);
        Assert.IsFalse(main.Hotkeys!.IsTaken(Gesture("Ctrl+Shift+B")));
        Assert.IsTrue(main.Hotkeys.IsTaken(Gesture("Ctrl+Alt+H")));
        main.Reload();
        Assert.IsEmpty(api.Registered);

        editor.General.RecordingHotkey(false);
        var (id, gesture) = api.Registered.Single();
        Assert.AreEqual("Ctrl+Shift+B", gesture.ToString());
        api.Press(id);
        Assert.AreEqual("hello.bat", launcher.Requests.Single().Script.Name);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
    }

    [TestMethod]
    public void CopyToMyScriptsLeavesTheHotkeyBehind()
    {
        using var test = new TestWorkspace();
        var (main, _) = Open(test, "Ctrl+Shift+B", trusted: true);

        main.MyScripts.CopyToMyScriptsCommand.Execute(main.Select("Workspace/Hello/hello.bat"));

        Assert.IsTrue(main.Details.Node!.IsMyScript);
        Assert.IsNull(UserEntries(main).Single().Hotkey);
        Assert.DoesNotContain("already the hotkey", main.LoadErrors ?? "");
    }

    [TestMethod]
    public void DuplicatingAMyScriptLeavesTheHotkeyBehind()
    {
        using var test = new TestWorkspace();
        WriteMyScript(test, "Ctrl+Alt+M");
        var main = test.OpenMain(test.CopyDemo(), trusted: true, launcher: new FakeLauncher(), shell: new FakeShell());
        main.Select("MyScripts/Mine");

        main.Details.DuplicateCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { "Ctrl+Alt+M", null }, UserEntries(main).Select(e => e.Hotkey).ToList());
        Assert.DoesNotContain("already the hotkey", main.LoadErrors ?? "");
    }

    [TestMethod]
    public void SavingAScriptWithoutAnIdAsAMyScriptLeavesTheHotkeyBehind()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var file = Path.Combine(demo, "batchpad.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace(HelloPs1, """{ "name": "hello.ps1", "path": "hello.ps1", "hotkey": "Ctrl+Alt+P" }"""));
        var main = test.OpenMain(demo, trusted: true, launcher: new FakeLauncher(), shell: new FakeShell());
        main.Select("Workspace/Hello/hello.ps1");

        main.Details.SaveAsMyScriptCommand.Execute(null);

        var entry = UserEntries(main).Single();
        Assert.AreEqual("hello.ps1", entry.Name);
        Assert.IsNull(entry.Hotkey);
        Assert.DoesNotContain("already the hotkey", main.LoadErrors ?? "");
    }

    [TestMethod]
    public void AKeyAnotherProgramHoldsShowsOnTheDetails()
    {
        using var test = new TestWorkspace();
        var api = new FakeHotkeyApi { Held = { "Ctrl+Shift+B" } };
        var (main, _) = Open(test, "Ctrl+Shift+B", trusted: true, api: api);

        main.Select("Workspace/Hello/hello.bat");

        Assert.IsEmpty(api.Registered);
        Assert.AreEqual("Ctrl+Shift+B is taken by another program.", main.Details.HotkeyProblem);
    }

    [TestMethod]
    public void ClearUnregistersEverything()
    {
        using var test = new TestWorkspace();
        var (main, api) = Open(test, "Ctrl+Shift+B", trusted: true);

        main.Hotkeys!.Clear();

        Assert.IsEmpty(api.Registered);
    }

    [TestMethod]
    public void TheWin32ApiRegistersOnAMessageOnlyWindowAndDeliversWmHotkey()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RegisterOnMessageOnlyWindow();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    private static void RegisterOnMessageOnlyWindow()
    {
        using var source = new HwndSource(new HwndSourceParameters("BatchPadHotkeyTest") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        var api = new Win32HotkeyApi(source);
        var pressed = new List<int>();
        api.Pressed += pressed.Add;
        Assert.IsTrue(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F24", out var gesture));

        Assert.IsTrue(api.Register(7, gesture));
        try
        {
            Assert.IsFalse(api.Register(8, gesture));
            SendMessage(source.Handle, Win32HotkeyApi.WmHotkey, 7, 0);
            CollectionAssert.AreEqual(new[] { 7 }, pressed);
        }
        finally
        {
            api.Unregister(7);
        }
        Assert.IsTrue(api.Register(9, gesture));
        api.Unregister(9);
    }

    [TestMethod]
    public void SettingAHotkeyInTheEditorSavesIt()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(test.CopyDemo(), trusted: true, launcher: new FakeLauncher(), shell: new FakeShell());
        var editor = EditHello(main);

        editor.General.Hotkey = "Ctrl+Shift+B";
        Assert.IsNull(editor.General.HotkeyWarning);
        editor.SaveCommand.Execute(null);

        StringAssert.Contains(File.ReadAllText(main.Workspace!.FilePath), "\"hotkey\": \"Ctrl+Shift+B\"");
        Assert.AreEqual("Ctrl+Shift+B", main.Details.HotkeyText);
    }

    [TestMethod]
    public void ClearingTheHotkeyRemovesTheField()
    {
        using var test = new TestWorkspace();
        var (main, _) = Open(test, "Ctrl+Shift+B", trusted: true);
        var editor = EditHello(main);
        Assert.AreEqual("Ctrl+Shift+B", editor.General.Hotkey);

        editor.General.Hotkey = "";
        editor.SaveCommand.Execute(null);

        Assert.DoesNotContain("hotkey", File.ReadAllText(main.Workspace!.FilePath));
    }

    [TestMethod]
    public void ADuplicateHotkeyWarnsAndStillSaves()
    {
        using var test = new TestWorkspace();
        WriteMyScript(test, "Ctrl+Shift+B");
        var main = test.OpenMain(test.CopyDemo(), trusted: true, launcher: new FakeLauncher(), shell: new FakeShell());
        var editor = EditHello(main);

        editor.General.Hotkey = "Ctrl+Shift+B";
        Assert.AreEqual("Ctrl+Shift+B is already the hotkey of 'Mine'.", editor.General.HotkeyWarning);
        editor.SaveCommand.Execute(null);

        StringAssert.Contains(File.ReadAllText(main.Workspace!.FilePath), "\"hotkey\": \"Ctrl+Shift+B\"");
    }

    [TestMethod]
    public void AKeyAnotherProgramHoldsWarnsInTheEditor()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(test.CopyDemo(), trusted: true, launcher: new FakeLauncher(), shell: new FakeShell());
        var api = new FakeHotkeyApi { Held = { "Ctrl+Alt+H" } };
        main.Hotkeys = new HotkeyService(api, main.RunByKey);
        var editor = EditHello(main);

        editor.General.Hotkey = "Ctrl+Alt+H";

        Assert.AreEqual("Ctrl+Alt+H is taken by another program.", editor.General.HotkeyWarning);
        Assert.IsEmpty(api.Registered);
    }

    [TestMethod]
    public void ThePaletteShowsEachItemsHotkey()
    {
        using var test = new TestWorkspace();
        var (main, _) = Open(test, "Ctrl+Shift+B", trusted: true);

        main.Palette.OpenCommand.Execute(null);

        Assert.AreEqual("Ctrl+Shift+B", main.Palette.Results.Single(i => i.Title == "hello.bat").Hotkey);
        Assert.IsNull(main.Palette.Results.Single(i => i.Title == "hello.py").Hotkey);
    }

    [TestMethod]
    public void TheHotkeyBoxRecordsOnlyCombinationsItCanRegister()
    {
        Assert.AreEqual("Ctrl+Shift+B", HotkeyBox.GestureFor(Key.B, ModifierKeys.Control | ModifierKeys.Shift)?.ToString());
        Assert.AreEqual("Ctrl+Alt+Shift+H", HotkeyBox.GestureFor(Key.H, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)?.ToString());
        Assert.IsNull(HotkeyBox.GestureFor(Key.B, ModifierKeys.None));
        Assert.IsNull(HotkeyBox.GestureFor(Key.LeftCtrl, ModifierKeys.Control));
    }

    private static ScriptEditorViewModel EditHello(MainViewModel main)
    {
        main.Select("Workspace/Hello/hello.bat");
        main.Details.EditCommand.Execute(null);
        return main.Details.Editor!;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, nint lParam);

    private static (MainViewModel, FakeHotkeyApi) Open(TestWorkspace test, string hotkey, bool trusted,
        FakeLauncher? launcher = null, FakeHotkeyApi? api = null)
    {
        var demo = test.CopyDemo();
        WriteHotkey(demo, hotkey);
        var main = test.OpenMain(demo, trusted, launcher: launcher ?? new FakeLauncher(), shell: new FakeShell());
        api ??= new FakeHotkeyApi();
        main.Hotkeys = new HotkeyService(api, main.RunByKey);
        return (main, api);
    }

    private static void SetHotkey(MainViewModel main, string? hotkey)
    {
        var file = main.Workspace!.FilePath;
        var text = File.ReadAllText(file);
        var start = text.IndexOf("{ \"id\": \"hello-bat\"", StringComparison.Ordinal);
        var end = text.IndexOf('}', start) + 1;
        File.WriteAllText(file, text[..start] + WithHotkey(hotkey) + text[end..]);
    }

    private static void WriteHotkey(string demo, string hotkey)
    {
        var file = Path.Combine(demo, "batchpad.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace(HelloBat, WithHotkey(hotkey)));
    }

    private static void WriteMyScript(TestWorkspace test, string hotkey)
    {
        var userFile = test.Paths.UserFile("batchpad-demo");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        File.WriteAllText(userFile, $$"""{ "scripts": [ { "id": "mine", "name": "Mine", "command": "echo hi", "hotkey": "{{hotkey}}" } ] }""");
    }

    private static List<ScriptNode> UserEntries(MainViewModel main) =>
        UserStore.For(main.Workspace!).Load().Scripts.OfType<ScriptNode>().ToList();

    private static HotkeyGesture Gesture(string text) =>
        HotkeyGesture.TryParse(text, out var gesture) ? gesture : throw new AssertFailedException($"'{text}' doesn't parse.");

    private static string WithHotkey(string? hotkey) =>
        hotkey is null ? HelloBat : HelloBat.Replace(" }", $", \"hotkey\": \"{hotkey}\" }}");

    private sealed class FakeHotkeyApi : IHotkeyApi
    {
        public Dictionary<int, HotkeyGesture> Registered { get; } = [];
        public HashSet<string> Held { get; } = [];
        public int RegisterCalls { get; private set; }

        public event Action<int>? Pressed;

        public bool Register(int id, HotkeyGesture gesture)
        {
            RegisterCalls++;
            if (Held.Contains(gesture.ToString()))
                return false;
            Registered[id] = gesture;
            return true;
        }

        public void Unregister(int id) => Registered.Remove(id);

        public void Press(int id) => Pressed?.Invoke(id);
    }
}
