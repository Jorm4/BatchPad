using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

/// <summary>The example game repo's use cases against its script headers and list lines, with the config built in the editors.</summary>
[TestClass]
public sealed class AdoptionTests
{
    [TestMethod]
    public void BuildsAnyAppInAnyConfigWithTheConfigSwitchSplit()
    {
        using var repo = new AdoptionRepo();
        repo.Select("Workspace/Build/Build");
        var apps = repo.Multi("apps");
        AssertLines(new[] { "KartRacer", "BikeTrials", "RobotManager", "SpaceTrader", "RallyRacer", "ChessTrainer" },
            apps.Items.Select(i => i.Label).ToList());
        repo.Choose("config", "Release ASan");
        repo.Check(apps, "SpaceTrader", "RallyRacer");

        AssertLines(new[] { @"cmd /d /v:off /s /c """"<ws>\build.bat"" --release --asan SpaceTrader RallyRacer""" },
            repo.RunScript());
    }

    [TestMethod]
    public async Task TheFavouriteBuildsReleaseThenLaunchesTheExeAndItsDuplicateAnotherApp()
    {
        using var repo = new AdoptionRepo();
        var source = repo.Select("Workspace/Run/Build & run");
        repo.Choose("config", "Release");
        repo.Choose("app", "SpaceTrader");
        Assert.IsTrue(repo.Main.MyScripts.Add(source, repo.Main.Tree!.Roots[0]));

        var favourite = repo.Select("MyScripts/Build Release & run SpaceTrader");
        await repo.RunWorkflow();
        AssertLines(new[]
        {
            @"cmd /d /v:off /s /c """"<ws>\build.bat"" --release SpaceTrader""",
            @"<ws>\build\release\bin\spacetrader.exe",
        }, repo.Steps.Lines());

        Assert.IsTrue(repo.Main.MyScripts.DuplicateEntry(favourite));
        repo.Select(repo.Main.Tree.Roots[0].Children.Last().AutomationId);
        repo.Choose("app", "RobotManager");
        repo.Steps.Clear();
        await repo.RunWorkflow();
        AssertLines(new[]
        {
            @"cmd /d /v:off /s /c """"<ws>\build.bat"" --release RobotManager""",
            @"<ws>\build\release\bin\robotmanager.exe",
        }, repo.Steps.Lines());
    }

    [TestMethod]
    public async Task ServeOpensTheReadyUrlAndStopRunsTheCompanionOnTheSamePort()
    {
        using var repo = new AdoptionRepo();
        var node = repo.Select("Workspace/Web/Serve web build");
        ((IntFieldViewModel)repo.Field("port")).Text = "8199";
        repo.Choose("webApp", "SpaceTrader");

        repo.Main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)repo.Main.Output.Tabs.Single();
        repo.Launcher.Started[0].Emit("Press Ctrl+C to stop.", OutputStream.Stdout);
        await Eventually(() => run.IsReady && repo.Shell.Opened.Count > 0);

        Assert.AreEqual("http://localhost:8199/SpaceTrader.html", run.ReadyUrl);
        AssertLines(new[] { "http://localhost:8199/SpaceTrader.html" }, repo.Shell.Opened);
        Assert.AreEqual(RunBadge.Ready, node.Badge);

        await repo.Main.Details.StopCommand.ExecuteAsync(null);
        await run.Finished;
        AssertLines(new[]
        {
            @"cmd /d /v:off /s /c """"<ws>\serve_web.bat"" 8199""",
            @"cmd /d /v:off /s /c """"<ws>\stop_web.bat"" 8199""",
        }, repo.Launcher.Requests.Select(r => repo.Show(repo.Plan(r).Single())).ToList());
    }

    [TestMethod]
    public async Task BuildWebAndServeRunsBothStepsWithTheDefaultPort()
    {
        using var repo = new AdoptionRepo();
        repo.Select("Workspace/Web/Build web & serve locally");
        repo.Choose("webApp", "RallyRacer");

        await repo.RunWorkflow();

        AssertLines(new[]
        {
            @"cmd /d /v:off /s /c """"<ws>\build_web.bat"" RallyRacer""",
            @"cmd /d /v:off /s /c """"<ws>\serve_web.bat"" 8123""",
        }, repo.Steps.Lines());
    }

    [TestMethod]
    public async Task PackageNamesTheAppAndOpensTheOutputFolderOnSuccess()
    {
        using var repo = new AdoptionRepo();
        repo.Select("Workspace/Web/Package for web deploy");
        repo.Choose("webApp", "RallyRacer");

        AssertLines(new[] { @"cmd /d /v:off /s /c """"<ws>\package_web.bat"" RallyRacer""" }, repo.RunScript());
        var run = (RunViewModel)repo.Main.Output.Tabs.Single();
        var output = Path.Combine(repo.Directory, "build", "package_web", "RallyRacer");
        Assert.AreEqual(output, run.Artifacts.Single().Artifact.Path);

        Directory.CreateDirectory(output);
        repo.Launcher.Started[0].Finish(RunOutcome.Exited, 0);
        await run.Finished;
        AssertLines(new[] { output }, repo.Shell.Opened);
    }

    [TestMethod]
    public async Task NoPickBuildsTestsOnlyAndAPickOfMoreThanNineSplitsTheBuild()
    {
        using var repo = new AdoptionRepo();
        repo.Select("Workspace/Test/Build & run unit tests");
        repo.Choose("config", "Release");

        await repo.RunWorkflow();
        AssertLines(new[]
        {
            @"cmd /d /v:off /s /c """"<ws>\build.bat"" --release --tests""",
            @"python -u <ws>\tools\run_tests.py --release",
        }, repo.Steps.Lines());

        var suites = repo.Multi("suites");
        Assert.HasCount(11, suites.Items);
        repo.Check(suites, [.. suites.Items.Select(i => i.Label)]);
        repo.Steps.Clear();
        await repo.RunWorkflow();

        const string first = "MathTests ContainersTests SpriteTests AudioTests InputTests SaveTests TileMapTests ScriptingTests MenuTests";
        AssertLines(new[]
        {
            $@"cmd /d /v:off /s /c """"<ws>\build.bat"" --release {first}""",
            @"cmd /d /v:off /s /c """"<ws>\build.bat"" --release PhysicsTests NetTests""",
            $@"python -u <ws>\tools\run_tests.py {first} PhysicsTests NetTests --release",
        }, repo.Steps.Lines());
    }

    [TestMethod]
    public async Task EachPickedBenchmarkRunsItsReleaseExeAtTheTier()
    {
        using var repo = new AdoptionRepo();
        repo.Select("Workspace/Test/Build & run benchmarks");
        repo.Check(repo.Multi("benches"), "ContainersBench", "PathfindBench");
        repo.Choose("tier", "Smoke");

        await repo.RunWorkflow();

        AssertLines(new[]
        {
            @"cmd /d /v:off /s /c """"<ws>\build.bat"" --release ContainersBench PathfindBench""",
            @"<ws>\build\release\benchmarks\containersbench.exe --benchmark_filter=^Smoke",
            @"<ws>\build\release\benchmarks\pathfindbench.exe --benchmark_filter=^Smoke",
        }, repo.Steps.Lines());
    }

    [TestMethod]
    public async Task ScenariosBuildAsanThenRunPytestFromTheQaFolderWithTheWindowVisible()
    {
        using var repo = new AdoptionRepo();
        repo.Select("Workspace/Test/Gameplay scenarios");
        repo.Choose("game", "SpaceTrader");
        repo.Choose("window", "Visible");

        await repo.RunWorkflow();

        AssertLines(new[]
        {
            @"cmd /d /v:off /s /c """"<ws>\build.bat"" --asan spacetrader""",
            @"python -u -m pytest --junitxml=<ws>/build/qa/scenarios.xml scenarios --game spacetrader --show-windows",
        }, repo.Steps.Lines());
        Assert.AreEqual(Path.Combine(repo.Directory, @"tools\qa"), repo.Steps.Recorded[1].Plan[0].Command.WorkingDirectory);
    }

    [TestMethod]
    public async Task CrawlsOneGamePerCallAndAlwaysWritesTheReport()
    {
        using var repo = new AdoptionRepo();
        repo.Select("Workspace/Test/QA crawl & use cases");
        var games = repo.Multi("game");
        CollectionAssert.Contains(games.Items.Select(i => i.Option.Value).ToList(), "robotmanager");
        repo.Check(games, "robotmanager", "spacetrader");
        repo.Steps.ExitCodes["qa-game"] = 1;

        var tab = await repo.RunWorkflow();

        AssertLines(new[]
        {
            @"cmd /d /v:off /s /c """"<ws>\build.bat"" --asan robotmanager spacetrader""",
            @"python -u -m pytest scenarios/test_crawl.py --game robotmanager",
            @"python -u -m pytest scenarios/test_crawl.py --game spacetrader",
            @"python -u <ws>\tools\qa\report.py",
        }, repo.Steps.Lines());
        Assert.AreEqual("failed", tab.StatusText);
        var report = tab.Steps.Last();
        Assert.AreEqual("exit 0", report.StatusText);
        var html = Path.Combine(repo.Directory, "qa_report.html");
        Assert.AreEqual(html, report.Artifacts.Single().Artifact.Path);
        AssertLines(new[] { html }, repo.Shell.Opened);
    }

    [TestMethod]
    public async Task TheQaReportOpensWhenRunOnItsOwn()
    {
        using var repo = new AdoptionRepo();
        repo.Select("Workspace/Test/QA report");

        AssertLines(new[] { @"python -u <ws>\tools\qa\report.py" }, repo.RunScript());
        var run = (RunViewModel)repo.Main.Output.Tabs.Single();
        repo.Launcher.Started[0].Finish(RunOutcome.Exited, 0);
        await run.Finished;

        AssertLines(new[] { Path.Combine(repo.Directory, "qa_report.html") }, repo.Shell.Opened);
    }

    [TestMethod]
    public void LinksOpenTheLocalReportAndTheWebPage()
    {
        using var repo = new AdoptionRepo();

        repo.Select("Workspace/Reports/QA report");
        repo.Main.Details.OpenLinkCommand.Execute(null);
        repo.Select("Workspace/Reports/Local web build");
        repo.Main.Details.OpenLinkCommand.Execute(null);

        AssertLines(new[] { Path.Combine(repo.Directory, "qa_report.html"), "http://localhost:8123/" }, repo.Shell.Opened);
    }

    [TestMethod]
    public void PlainScriptsAreDiscoveredAndRunWithoutArguments()
    {
        using var repo = new AdoptionRepo();
        Assert.IsEmpty(repo.Main.Workspace!.Errors, string.Join('\n', repo.Main.Workspace.Errors));
        var discovered = repo.Main.Tree!.Roots[1].Descendants().Where(n => n.Kind == NodeKind.Script && n.Item?.HasEntry == false)
            .Select(n => n.AutomationId).ToList();
        CollectionAssert.AreEquivalent(new[] { "Workspace/Regen assets", "Workspace/patches/Apply" }, discovered, string.Join(", ", discovered));

        repo.Select("Workspace/patches/Apply");
        AssertLines(new[] { @"python -u <ws>\tools\patches\apply.py" }, repo.RunScript());
        repo.Launcher.Requests.Clear();
        repo.Select("Workspace/Build/Generate");
        AssertLines(new[] { @"cmd /d /v:off /s /c """"<ws>\generate.bat""""" }, repo.RunScript());
    }

    private static void AssertLines(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.AreEqual(string.Join(Environment.NewLine, expected), string.Join(Environment.NewLine, actual));

}

internal sealed class AdoptionRepo : IDisposable
{
    private static readonly string Fixture = AdoptionConfig.Fixture;
    private static readonly string[] PlaceholderExes =
    [
        @"build\release\bin\spacetrader.exe", @"build\release\bin\robotmanager.exe",
        @"build\release\benchmarks\containersbench.exe", @"build\release\benchmarks\pathfindbench.exe",
    ];

    private readonly TestWorkspace _test = new();

    public AdoptionRepo()
    {
        Directory = Path.Combine(_test.Root, "repo");
        CopyTree(Fixture, Directory);
        foreach (var exe in PlaceholderExes)
            Write(exe, []);
        File.WriteAllText(Path.Combine(Directory, "batchpad.json"), AdoptionConfig.Built);

        var gate = new TrustStore(new Settings(), Path.Combine(_test.Root, "gate.json"));
        gate.Trust(Directory);
        Steps = new RecordingSteps(new RunGate(gate), Interpreters, this);
        Main = new MainViewModel(_test.Paths, new Settings(), Launcher, shell: Shell, confirm: new FakeConfirm(),
            workflows: new RecordingWorkflows(Steps, new ShellOpener(Shell)));
        Main.Trust.Trust(Directory);
        Main.OpenInitial(Directory, _test.Root);
    }

    public string Directory { get; }
    public InterpreterLocator Interpreters { get; } = new();
    public FakeLauncher Launcher { get; } = new();
    public FakeShell Shell { get; } = new();
    public RecordingSteps Steps { get; }
    public MainViewModel Main { get; }

    public NodeViewModel Select(string automationId) => Main.Select(automationId);

    public ParameterFieldViewModel Field(string name) =>
        Main.Details.Form!.Field(name) ?? throw new AssertFailedException($"No field {name}.");

    public MultichoiceFieldViewModel Multi(string name) => (MultichoiceFieldViewModel)Field(name);

    public void Choose(string name, string label)
    {
        var field = (ChoiceFieldViewModel)Field(name);
        field.Selected = field.Options.Single(o => o.Label == label);
    }

    public void Check(MultichoiceFieldViewModel field, params string[] labelsOrValues)
    {
        foreach (var item in field.Items)
            item.IsChecked = labelsOrValues.Contains(item.Label) || labelsOrValues.Contains(item.Option.Value);
    }

    public List<string> RunScript()
    {
        Main.Details.RunCommand.Execute(null);
        return Plan(Launcher.Requests.Last()).Select(Show).ToList();
    }

    public async Task<WorkflowRunViewModel> RunWorkflow()
    {
        Main.Details.RunCommand.Execute(null);
        var tab = (WorkflowRunViewModel)Main.Output.Tabs.Last();
        await tab.Finished.WaitAsync(Limit);
        return tab;
    }

    public IReadOnlyList<RunSpec> Plan(RunRequest request) => RunPlanner.Plan(request, Interpreters);

    public string Show(RunSpec spec)
    {
        var text = spec.Command.Display.Replace(Interpreters.Cmd, "cmd").Replace(Directory, "<ws>");
        return Interpreters.Python() is { } python
            ? text.Replace($"{ArgvQuoter.Quote(python.Path)} {string.Join(' ', python.LeadingArguments)}", "python")
            : text;
    }

    private void Write(string relativePath, byte[] content)
    {
        var path = Path.Combine(Directory, relativePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public void Dispose()
    {
        foreach (var tab in Main.Output.Tabs)
            tab.Dispose();
        _test.Dispose();
    }
}

/// <summary>Records each workflow step's planned command lines, then runs <c>exit N</c> in its place.</summary>
internal sealed class RecordingSteps(RunGate gate, InterpreterLocator interpreters, AdoptionRepo repo) : IStepLauncher
{
    private readonly Lock _lock = new();

    public List<(RunRequest Request, IReadOnlyList<RunSpec> Plan)> Recorded { get; } = [];
    public Dictionary<string, int> ExitCodes { get; } = [];

    public List<string> Lines()
    {
        lock (_lock)
            return [.. Recorded.SelectMany(r => r.Plan).Select(repo.Show)];
    }

    public void Clear()
    {
        lock (_lock)
            Recorded.Clear();
    }

    public RunHandle Start(RunRequest request)
    {
        var plan = RunPlanner.Plan(request, interpreters);
        lock (_lock)
            Recorded.Add((request, plan));
        var exitCode = ExitCodes.GetValueOrDefault(request.Script.Id ?? "");
        var standIn = new CommandLine(interpreters.Cmd, $"/d /c exit {exitCode}", request.Workspace.Directory);
        return gate.Start(request.Workspace, [new RunSpec(standIn, plan[0].Environment)]);
    }

    public Task StopAsync(RunHandle run, RunRequest request) => run.StopAsync();
}

internal sealed class RecordingWorkflows(RecordingSteps steps, IShellOpener opener) : IWorkflowLauncher
{
    public WorkflowRun Start(LoadedWorkspace workspace, WorkflowRequest request) => new WorkflowRunner(workspace, steps, opener).Start(request);

    public Task StopStepAsync(RunHandle run, RunRequest request) => steps.StopAsync(run, request);
}
