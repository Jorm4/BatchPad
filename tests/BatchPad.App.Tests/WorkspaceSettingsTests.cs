using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class WorkspaceSettingsTests
{
    [TestMethod]
    public void AToolsFolderShowsItsMatchesBeforeSavingAndAfterReload()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var tools = Directory.CreateDirectory(Path.Combine(demo, "tools")).FullName;
        foreach (var file in new[] { "a.py", "b.py", "c.bat" })
            File.WriteAllText(Path.Combine(tools, file), "");
        var main = test.OpenMain(demo, trusted: true);

        main.OpenWorkspaceSettingsCommand.Execute(null);
        var settings = main.WorkspaceSettings!;
        settings.AddFolderCommand.Execute(null);
        var folder = settings.ScriptFolders[^1];
        folder.Path = "tools";
        folder.Include = "*.py";

        CollectionAssert.AreEqual(new[] { "tools/a.py", "tools/b.py" }, folder.Matches.ToList());
        settings.SaveCommand.Execute(null);

        Assert.IsNull(main.WorkspaceSettings);
        Assert.IsNotNull(main.Tree!.Find("Workspace/A"));
        main.OpenWorkspaceSettingsCommand.Execute(null);
        CollectionAssert.AreEqual(new[] { "tools/a.py", "tools/b.py" }, main.WorkspaceSettings!.Folder("tools")!.Matches.ToList());
    }

    [TestMethod]
    public void EditingASharedParametersChoicesChangesEveryScriptThatUsesIt()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        ConfigWriter.Update(Path.Combine(demo, "batchpad.json"), file =>
        {
            file.SharedParams = new()
            {
                ["app"] = new ParameterDefinition { Type = ParameterType.Choice, Arg = "--app", Choices = [new() { Value = "A" }, new() { Value = "B" }] },
            };
            foreach (var script in ((FolderNode)file.Scripts[0]).Items.OfType<ScriptNode>().Take(2))
                script.Params = [new ParameterDefinition { Use = "app" }];
        });
        var main = test.OpenMain(demo, trusted: true);

        main.OpenWorkspaceSettingsCommand.Execute(null);
        var app = main.WorkspaceSettings!.Shared("app")!;
        Assert.AreEqual("Used by Hello › hello.bat, Hello › hello.py", app.UsedBySummary);
        app.Choices.AddRowCommand.Execute(null);
        app.Choices.Rows[^1].Value = "C";
        main.WorkspaceSettings.SaveCommand.Execute(null);

        foreach (var id in new[] { "Workspace/Hello/hello.bat", "Workspace/Hello/hello.py" })
        {
            main.Tree!.Find(id)!.IsSelected = true;
            var field = (ChoiceFieldViewModel)main.Details.Form!.Field("app")!;
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, field.Options.Select(o => o.Value).ToList(), id);
        }
    }

    [TestMethod]
    public void ANewLinkToHtmlOpensAtOnceWhileOneToABatchFileAsksFirst()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var shell = new FakeShell();
        var confirm = new FakeConfirm { Answer = false };
        var main = new MainViewModel(test.Paths, new Settings(), new FakeLauncher(), shell: shell, confirm: confirm);
        main.Trust.Trust(demo);
        main.OpenInitial(demo, test.Root);

        AddLink(main, "Docs", "docs/index.html");
        main.Details.OpenLinkCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { Path.Combine(demo, "docs", "index.html") }, shell.Opened);
        Assert.IsEmpty(confirm.Asked);

        AddLink(main, "Build", "hello.bat");
        main.Details.OpenLinkCommand.Execute(null);
        Assert.HasCount(1, confirm.Asked);
        Assert.HasCount(1, shell.Opened);

        confirm.Answer = true;
        main.Details.OpenLinkCommand.Execute(null);
        Assert.AreEqual(Path.Combine(demo, "hello.bat"), shell.Opened[^1]);
    }

    [TestMethod]
    public void NewFolderGoesIntoTheExplicitFolderItWasAskedFrom()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var main = test.OpenMain(demo, trusted: true);

        main.Tree!.NewFolderCommand.Execute(main.Tree.Find("Workspace/Hello/hello.py"));
        main.NewItem!.Name = "Nested";
        main.NewItem.SaveCommand.Execute(null);

        Assert.IsNotNull(main.Tree!.Find("Workspace/Hello/Nested"));
        Assert.IsTrue(main.Tree.Find("Workspace/Hello/Nested")!.IsSelected);
    }

    private static void AddLink(MainViewModel main, string name, string target)
    {
        main.Tree!.NewLinkCommand.Execute(null);
        main.NewItem!.Name = name;
        main.NewItem.Target = target;
        main.NewItem.SaveCommand.Execute(null);
        Assert.AreEqual(name, main.Details.Node!.Name);
    }
}

internal sealed class FakeConfirm : IConfirmService
{
    public bool Answer { get; set; }
    public List<string> Asked { get; } = [];

    public bool Confirm(string title, string message)
    {
        Asked.Add(message);
        return Answer;
    }

    public bool? CancellableAnswer { get; set; }

    public bool? ConfirmOrCancel(string title, string message)
    {
        Asked.Add(message);
        return CancellableAnswer;
    }
}
