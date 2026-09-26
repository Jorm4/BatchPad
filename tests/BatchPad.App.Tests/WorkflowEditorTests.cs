using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class WorkflowEditorTests
{
    [TestMethod]
    public void AddingTwoStepsAndSavingWritesAStepsArrayWithIds()
    {
        using var test = new TestWorkspace();
        var main = OpenShipWorkspace(test);
        main.Tree!.NewWorkflowCommand.Execute(main.Tree.Roots[1]);
        var editor = main.Details.WorkflowEditor!;

        editor.Name = "Build then run";
        Assert.IsTrue(editor.AddStep(main.Tree.Find("Workspace/Build")!));
        Assert.IsTrue(editor.AddStep(main.Tree.Find("Workspace/Run app")!));
        editor.MoveStep(editor.Steps[1], 0);
        editor.SaveCommand.Execute(null);

        var saved = Workflows(main).Single(w => w.Name == "Build then run");
        CollectionAssert.AreEqual(new[] { "run-app", "build" }, saved.Steps.Select(s => s.Id).ToList());
        CollectionAssert.AreEqual(new[] { "workspace:run-app", "workspace:build" }, saved.Steps.Select(s => s.Run).ToList());
        Assert.IsInstanceOfType<WorkflowNode>(main.SelectedNode!.Node);
    }

    [TestMethod]
    public void ForEachOverAMultichoiceWritesForEachWithItem()
    {
        using var test = new TestWorkspace();
        var main = OpenShipWorkspace(test);
        var editor = EditShip(main);
        var card = editor.Steps.Single(s => s.Id == "run-app");

        card.IsForEach = true;

        Assert.AreEqual("games", card.ForEachParameter);
        Assert.AreEqual("app", card.ItemTarget);
        Assert.IsNull(card.Form!.Field("app"));
        CollectionAssert.Contains(card.FlowChips.ToList(), "app ← each games");
        editor.SaveCommand.Execute(null);

        var step = Workflows(main).Single(w => w.Id == "ship").Steps.Single(s => s.Id == "run-app");
        Assert.AreEqual("${param:games}", step.ForEach);
        Assert.AreEqual("${item}", step.Values!["app"]!.GetValue<string>());
    }

    [TestMethod]
    public void FlowChipsListTheStepsThatReceiveAParameter()
    {
        using var test = new TestWorkspace();
        var main = OpenShipWorkspace(test);
        var editor = EditShip(main);

        Assert.AreEqual("app → Build, Run app", editor.ParameterChips.Single(c => c.Name == "app").Text);
        CollectionAssert.Contains(editor.Steps.Single(s => s.Id == "build").FlowChips.ToList(), "app ← app");
        Assert.AreEqual("Always", editor.Steps.Single(s => s.Id == "report").When.Label);

        var buildApp = (ChoiceFieldViewModel)editor.Steps[0].Form!.Field("app")!;
        buildApp.Selected = buildApp.Options[0];
        Assert.AreEqual("app → Run app", editor.ParameterChips.Single(c => c.Name == "app").Text);
    }

    [TestMethod]
    public async Task ARunOfTheDemoWorkflowShowsTwoStepRowsEndingExit0()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var main = new MainViewModel(test.Paths, new Settings(), shell: new FakeShell(), confirm: new FakeConfirm());
        main.Trust.Trust(demo);
        main.OpenInitial(demo, test.Root);
        main.Tree!.Find("Workspace/Build & run")!.IsSelected = true;
        Assert.IsTrue(main.Details.IsWorkflow);
        Assert.AreEqual("build → run", main.Details.Preview);

        main.Details.RunCommand.Execute(null);
        var tab = (WorkflowRunViewModel)main.Output.Tabs.Single();
        await tab.Finished.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual("passed", tab.StatusText);
        CollectionAssert.AreEqual(new[] { "exit 0", "exit 0" }, tab.Steps.Select(s => s.StatusText).ToList());
        CollectionAssert.AreEqual(new[] { "✓", "✓" }, tab.Steps.Select(s => s.Glyph).ToList());
        Assert.IsTrue(tab.Steps[1].Lines.Any(l => l.Text.Contains("Alpha")), string.Join("\n", tab.Steps[1].Lines));
        Assert.AreEqual(RunBadge.Passed, main.SelectedNode!.Badge);
    }

    [TestMethod]
    public void GroupingTwoCardsWritesAParallelArrayWithBothSteps()
    {
        using var test = new TestWorkspace();
        var main = OpenShipWorkspace(test);
        var editor = EditShip(main);

        editor.Group(editor.Steps[1], editor.Steps[0]);

        Assert.HasCount(2, editor.Steps);
        Assert.IsTrue(editor.Steps[0].IsGroup);
        editor.SaveCommand.Execute(null);
        var steps = Workflows(main).Single(w => w.Id == "ship").Steps;
        CollectionAssert.AreEqual(new[] { "build", "run-app" }, steps[0].Parallel!.Select(s => s.Id).ToList());
        Assert.IsNull(steps[0].Run);
        Assert.AreEqual("report", steps[1].Id);
    }

    [TestMethod]
    public void UngroupingRestoresTheSteps()
    {
        using var test = new TestWorkspace();
        var main = OpenShipWorkspace(test);
        var editor = EditShip(main);
        editor.Group(editor.Steps[1], editor.Steps[0]);
        editor.SaveCommand.Execute(null);
        editor = EditShip(main);

        editor.Ungroup(editor.Steps[0]);
        editor.SaveCommand.Execute(null);

        var steps = Workflows(main).Single(w => w.Id == "ship").Steps;
        CollectionAssert.AreEqual(new[] { "build", "run-app", "report" }, steps.Select(s => s.Id).ToList());
        Assert.IsTrue(steps.All(s => s.Parallel is null));
    }

    [TestMethod]
    public void RunOptionsRoundTripThroughSaveAndReload()
    {
        using var test = new TestWorkspace();
        var main = OpenShipWorkspace(test);
        var editor = EditShip(main);
        var build = editor.Steps.Single(s => s.Id == "build");
        var run = editor.Steps.Single(s => s.Id == "run-app");

        build.ContinueOnError = true;
        build.Confirm = true;
        build.RetryCount = "2";
        build.RetryDelay = "5";
        run.IsForEach = true;
        run.ParallelDegree = "3";
        run.FailFast = true;
        editor.SaveCommand.Execute(null);

        var saved = Workflows(main).Single(w => w.Id == "ship").Steps;
        Assert.AreEqual(2, saved[0].Retry!.Count);
        Assert.AreEqual(3, saved[1].MaxParallel);
        editor = EditShip(main);
        build = editor.Steps.Single(s => s.Id == "build");
        run = editor.Steps.Single(s => s.Id == "run-app");
        Assert.IsTrue(build.ContinueOnError);
        Assert.IsTrue(build.Confirm);
        Assert.AreEqual("2", build.RetryCount);
        Assert.AreEqual("5", build.RetryDelay);
        Assert.AreEqual("3", run.ParallelDegree);
        Assert.IsTrue(run.FailFast);
        Assert.IsFalse(editor.Steps.Single(s => s.Id == "report").ContinueOnError);
    }

    [TestMethod]
    public async Task ARunOfANestedWorkflowListsTheInnerStepsUnderItsRow()
    {
        using var test = new TestWorkspace();
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "nested")).FullName;
        File.WriteAllText(Path.Combine(directory, "tool.bat"), "@echo %*\r\n");
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
            { "id": "nested", "scripts": [
              { "id": "tool", "name": "Tool", "path": "tool.bat" },
              { "id": "inner", "name": "Inner", "steps": [ { "id": "one", "run": "tool" }, { "id": "two", "run": "tool" } ] },
              { "id": "outer", "name": "Outer", "steps": [
                { "parallel": [ { "id": "a", "run": "tool" }, { "id": "nest", "run": "inner" } ] },
                { "id": "last", "run": "tool" } ] } ] }
            """);
        var main = test.OpenMain(directory, trusted: true);
        main.Tree!.Find("Workspace/Outer")!.IsSelected = true;

        main.Details.RunCommand.Execute(null);
        var tab = (WorkflowRunViewModel)main.Output.Tabs.Single();
        await tab.Finished.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.AreEqual("passed", tab.StatusText);
        CollectionAssert.AreEqual(new[] { "step1", "a", "nest", "one", "two", "last" }, tab.Steps.Select(s => s.Step.Id).ToList());
        CollectionAssert.AreEqual(new[] { 0, 1, 1, 2, 2, 0 }, tab.Steps.Select(s => s.Depth).ToList());
        Assert.AreEqual("Parallel", tab.Steps[0].Name);
        Assert.IsTrue(tab.Steps.All(s => s.Glyph == "✓"), string.Join(", ", tab.Steps.Select(s => s.StatusText)));
    }

    [TestMethod]
    public async Task AStepLogHasSearchErrorNavigationAndSourceLinks()
    {
        using var test = new TestWorkspace();
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "links")).FullName;
        File.WriteAllText(Path.Combine(directory, "build.bat"),
            "@echo compiling\r\n@echo build.bat(2): error E1: bad\r\n@echo ::set version=1.2.3\r\n@echo done\r\n");
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
            { "id": "links", "scripts": [
              { "id": "build", "name": "Build", "path": "build.bat", "errorPatterns": [ "error E\\d" ] },
              { "id": "flow", "name": "Flow", "steps": [ { "id": "build", "run": "build" } ] } ] }
            """);
        var shell = new FakeShell();
        var main = new MainViewModel(test.Paths, new Settings { EditorCommand = "edit \"{file}\" {line}" }, shell: shell, confirm: new FakeConfirm());
        main.Trust.Trust(directory);
        main.OpenInitial(directory, test.Root);
        main.Tree!.Find("Workspace/Flow")!.IsSelected = true;

        main.Details.RunCommand.Execute(null);
        var tab = (WorkflowRunViewModel)main.Output.Tabs.Single();
        await tab.Finished.WaitAsync(TimeSpan.FromSeconds(15));
        var step = tab.Steps.Single();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (step.Lines.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        CollectionAssert.AreEqual(new[] { "compiling", "build.bat(2): error E1: bad", "done" }, step.Lines.Select(l => l.Text).ToList());
        Assert.AreEqual("version=1.2.3", step.OutputsText);
        Assert.AreSame(step.Log, tab.Log);
        tab.Log!.NextErrorCommand.Execute(null);
        Assert.AreEqual("build.bat(2): error E1: bad", step.Log.SelectedLine?.Text);
        Assert.IsFalse(tab.AutoScroll);
        step.Log.SearchText = "DONE";
        CollectionAssert.AreEqual(new[] { "done" }, step.Log.DisplayedLines.Select(l => l.Text).ToList());
        step.Lines[1].Links!.Single().OpenCommand.Execute(null);
        CollectionAssert.AreEqual(new[] { $"edit \"{Path.Combine(directory, "build.bat")}\" 2" }, shell.Commands);
    }

    private static WorkflowEditorViewModel EditShip(MainViewModel main)
    {
        main.Tree!.Find("Workspace/Ship")!.IsSelected = true;
        main.Details.EditCommand.Execute(null);
        return main.Details.WorkflowEditor!;
    }

    private static List<WorkflowNode> Workflows(MainViewModel main) =>
        ConfigReader.ReadFile(main.Workspace!.FilePath).Scripts.OfType<WorkflowNode>().ToList();

    private static MainViewModel OpenShipWorkspace(TestWorkspace test)
    {
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "ship")).FullName;
        File.WriteAllText(Path.Combine(directory, "tool.bat"), "@echo %*\r\n");
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
            {
              "id": "ship-demo",
              "scripts": [
                { "id": "build", "name": "Build", "path": "tool.bat", "params": [
                    { "name": "config", "type": "choice", "choices": ["debug", "release"] },
                    { "name": "app", "type": "choice", "choices": ["SpaceTrader", "RallyRacer"] } ] },
                { "id": "run-app", "name": "Run app", "path": "tool.bat", "params": [
                    { "name": "app", "type": "choice", "choices": ["SpaceTrader", "RallyRacer"] } ] },
                { "id": "report", "name": "Report", "path": "tool.bat" },
                { "id": "ship", "name": "Ship",
                  "params": [
                    { "name": "app", "type": "choice", "choices": ["SpaceTrader", "RallyRacer"] },
                    { "name": "games", "type": "multichoice", "choices": ["SpaceTrader", "RallyRacer"] } ],
                  "steps": [
                    { "id": "build", "run": "build" },
                    { "id": "run-app", "run": "run-app" },
                    { "id": "report", "run": "report", "when": "always" } ] }
              ]
            }
            """);
        var main = new MainViewModel(test.Paths, new Settings(), new FakeLauncher(), shell: new FakeShell(), confirm: new FakeConfirm());
        main.Trust.Trust(directory);
        main.OpenInitial(directory, test.Root);
        return main;
    }
}
