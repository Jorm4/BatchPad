using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Wizard;
using BatchPad.Core.Config;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class NewWorkspaceWizardTests
{
    [TestMethod]
    public void TheChecklistProposesRunnableScriptsAndFinishingWritesATrustedWorkspace()
    {
        using var test = new TestWorkspace();
        var project = Directory.CreateDirectory(Path.Combine(test.Root, "project")).FullName;
        File.WriteAllText(Path.Combine(project, "a.bat"), "@rem Builds everything.\r\n@echo a\r\n");
        Directory.CreateDirectory(Path.Combine(project, "tools"));
        File.WriteAllText(Path.Combine(project, "tools", "b.py"), "\"\"\"Regenerates assets.\"\"\"\nprint('b')\n");
        File.WriteAllText(Path.Combine(project, "tools", "test_c.py"), "print('c')\n");
        var main = new MainViewModel(test.Paths, new Settings());

        main.OpenNewWorkspaceCommand.Execute(null);
        var wizard = main.NewWorkspace!;
        wizard.Folder = project;
        wizard.NextCommand.Execute(null);

        Assert.AreEqual(WizardPage.Scripts, wizard.Page);
        var checks = wizard.Scripts.ToDictionary(s => s.RelativePath, s => s.IsChecked);
        CollectionAssert.AreEquivalent(new[] { "a.bat", "tools/b.py", "tools/test_c.py" }, checks.Keys);
        Assert.IsTrue(checks["a.bat"]);
        Assert.IsTrue(checks["tools/b.py"]);
        Assert.IsFalse(checks["tools/test_c.py"]);
        Assert.AreEqual("Regenerates assets.", wizard.Scripts.Single(s => s.RelativePath == "tools/b.py").Description);

        wizard.NextCommand.Execute(null);
        Assert.AreEqual("project", wizard.Name);
        wizard.FinishCommand.Execute(null);

        var file = ConfigReader.ReadFile(Path.Combine(project, "batchpad.json"));
        Assert.IsFalse(string.IsNullOrEmpty(file.Id));
        Assert.AreEqual(WorkspaceFile.SchemaUrl, file.Schema);
        var covered = ScriptFolderScanner.Scan(project, file.ScriptFolders!).Select(s => s.RelativePath);
        CollectionAssert.AreEquivalent(new[] { "a.bat", "tools/b.py" }, covered.ToList());
        Assert.IsTrue(main.Trust.IsTrusted(project));
        Assert.IsNull(main.NewWorkspace);
        Assert.AreEqual(Path.Combine(project, "batchpad.json"), main.Workspace!.FilePath, ignoreCase: true);
        Assert.IsTrue(main.IsTrusted);
    }

    [TestMethod]
    public void AFolderThatAlreadyHasAWorkspaceIsRefused()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var main = new MainViewModel(test.Paths, new Settings());

        main.OpenNewWorkspaceCommand.Execute(null);
        main.NewWorkspace!.Folder = demo;
        main.NewWorkspace.NextCommand.Execute(null);

        Assert.AreEqual(WizardPage.Folder, main.NewWorkspace.Page);
        Assert.IsNotNull(main.NewWorkspace.Error);
    }
}
