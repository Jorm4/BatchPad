using System.Runtime.InteropServices;
using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class JumpListTests
{
    private const string Entries = """
        { "scripts": [
          { "id": "hi", "name": "Hi", "base": "workspace:hello-bat", "pinned": true },
          { "id": "gone", "name": "Gone", "base": "workspace:missing", "pinned": true },
          { "id": "plain", "name": "Plain", "base": "workspace:hello-bat" } ] }
        """;

    [TestMethod]
    public void PinningWritesPinnedAndUnpinningRemovesIt()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: new FakeLauncher(), shell: new FakeShell());
        main.MyScripts.AddToMyScriptsCommand.Execute(main.Select("Workspace/Hello/hello.bat"));
        var userFile = test.Paths.UserFile(main.Workspace!.Id);

        var entry = main.Tree!.MyScriptsRoot.Children.Single();
        Assert.IsTrue(main.MyScripts.PinCommand.CanExecute(entry));
        main.MyScripts.PinCommand.Execute(entry);
        StringAssert.Contains(File.ReadAllText(userFile), "\"pinned\": true");

        entry = main.Tree.MyScriptsRoot.Children.Single();
        Assert.IsFalse(main.MyScripts.PinCommand.CanExecute(entry));
        main.MyScripts.UnpinCommand.Execute(entry);
        Assert.DoesNotContain("pinned", File.ReadAllText(userFile));
    }

    [TestMethod]
    public void TheDetailsPanelTogglesThePin()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: new FakeLauncher(), shell: new FakeShell());
        main.Select("Workspace/Hello/hello.bat");
        main.Details.SaveAsMyScriptCommand.Execute(null);
        Assert.AreEqual("Pin to jump list", main.Details.PinText);

        main.Details.TogglePinCommand.Execute(null);

        Assert.AreEqual("Unpin from jump list", main.Details.PinText);
        Assert.IsTrue(MyScriptsViewModel.CanUnpin(main.SelectedNode));
    }

    [TestMethod]
    public void EachPinnedEntryRunsItselfThenTheRecentWorkspacesFollow()
    {
        using var test = new TestWorkspace();
        var main = OpenWithEntries(test);
        var file = main.Workspace!.FilePath;
        var hi = main.Tree!.Find("MyScripts/Hi")!;
        var recent = Path.Combine(test.Root, "other", "batchpad.json");
        Assert.IsTrue(main.Tree.Find("MyScripts/Gone")!.IsBroken);

        var items = JumpListBuilder.Build(main.Tree, file, [file, recent]);

        CollectionAssert.AreEqual(new[]
        {
            new JumpListItem("Hi", ArgvQuoter.Join(["--run", hi.Key, "--workspace", file]), hi.Description),
            new JumpListItem("demo", ArgvQuoter.Quote(file), file, JumpListBuilder.RecentCategory),
            new JumpListItem("other", ArgvQuoter.Quote(recent), recent, JumpListBuilder.RecentCategory),
        }, items.ToArray());
    }

    [TestMethod]
    public void APinnedEntrysArgumentsSplitBackIntoItsKeyAndWorkspace()
    {
        using var test = new TestWorkspace();
        var main = OpenWithEntries(test, """
            { "scripts": [ { "id": "odd \"one\\", "name": "Odd", "base": "workspace:hello-bat", "pinned": true } ] }
            """);
        var odd = main.Tree!.Find("MyScripts/Odd")!;
        StringAssert.EndsWith(odd.Key, "odd \"one\\");

        var item = JumpListBuilder.Build(main.Tree, main.Workspace!.FilePath, []).Single();

        Assert.AreEqual(new GuiArguments(main.Workspace.FilePath, odd.Key), GuiArguments.Parse(SplitCommandLine(item.Arguments)));
    }

    [TestMethod]
    public void ADuplicateOfAPinnedEntryIsNotPinned()
    {
        using var test = new TestWorkspace();
        var main = OpenWithEntries(test);

        Assert.IsTrue(main.MyScripts.DuplicateEntry(main.Tree!.Find("MyScripts/Hi")!));

        Assert.IsTrue(MyScriptsViewModel.IsPinned(main.Tree.Find("MyScripts/Hi")));
        Assert.IsFalse(MyScriptsViewModel.IsPinned(main.SelectedNode));
        Assert.AreEqual("Hi (copy)", main.SelectedNode!.Name);
    }

    [TestMethod]
    public void AReloadAfterAnExternalPinRebuildsTheList()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: new FakeLauncher(), shell: new FakeShell());
        var jumpList = new FakeJumpList();
        main.JumpList = jumpList;
        Assert.IsFalse(jumpList.Last!.Any(i => i.Category is null));

        File.WriteAllText(test.Paths.UserFile(main.Workspace!.Id), Entries);
        main.Reload();

        Assert.AreEqual("Hi", jumpList.Last!.Single(i => i.Category is null).Title);
    }

    private static MainViewModel OpenWithEntries(TestWorkspace test, string entries = Entries)
    {
        var userFile = test.Paths.UserFile("batchpad-demo");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        File.WriteAllText(userFile, entries);
        return test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: new FakeLauncher(), shell: new FakeShell());
    }

    private static string[] SplitCommandLine(string arguments)
    {
        var argv = CommandLineToArgvW($"BatchPad.exe {arguments}", out var count);
        try
        {
            return [.. Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed class FakeJumpList : IJumpListService
    {
        public IReadOnlyList<JumpListItem>? Last { get; private set; }

        public void Apply(IReadOnlyList<JumpListItem> items) => Last = items;
    }
}
