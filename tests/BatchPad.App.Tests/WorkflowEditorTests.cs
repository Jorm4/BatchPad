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
