using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Editor;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.App.ViewModels.Workspace;
using BatchPad.Core.Config;
using BatchPad.Core.Model;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class AdoptionEditorTests
{
    [TestMethod]
    public void TheAdoptionConfigBuiltInTheEditorsReadsBackEqualToTheFixture()
    {
        var fixture = ConfigWriter.Serialize(ConfigReader.ReadFile(Path.Combine(AdoptionConfig.Fixture, "batchpad.json")));
        var built = ConfigWriter.Serialize(ConfigReader.Parse(AdoptionConfig.Built));

        Assert.AreEqual(fixture, built);
    }
}

/// <summary>Builds the adoption fixture's <c>batchpad.json</c> through the editors, once per test run.</summary>
internal static class AdoptionConfig
{
    public static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "adoption");

    private static readonly Lazy<string> Lazy = new(Build);

    public static string Built => Lazy.Value;

    private static string Build()
    {
        using var test = new TestWorkspace();
        var directory = Path.Combine(test.Root, "repo");
        CopyTree(Fixture, directory);
        var configFile = Path.Combine(directory, "batchpad.json");
        File.WriteAllText(configFile, """{ "id": "adoption-fixture" }""");

        var main = test.OpenMain(directory, launcher: new FakeLauncher(), shell: new FakeShell(), confirm: new FakeConfirm());
        new Builder(main).Run();
        return File.ReadAllText(configFile);
    }

    private sealed class Builder(MainViewModel main)
    {
        public void Run()
        {
            Settings();
            foreach (var folder in new[] { "Build", "Run", "Web", "Test", "Reports" })
                NewItem(main.Tree!.Roots[1], NewItemKind.Folder, folder);
            BuildFolder();
            RunFolder();
            WebFolder();
            TestFolder();
            NewItem(Node("Workspace/Reports"), NewItemKind.Link, "QA report", "qa_report.html");
            NewItem(Node("Workspace/Reports"), NewItemKind.Link, "Local web build", "http://localhost:8123/");
        }

        private void Settings()
        {
            main.OpenWorkspaceSettingsCommand.Execute(null);
            var settings = main.WorkspaceSettings!;
            settings.Name = "Game Studio";
            var root = settings.ScriptFolders.Single();
            root.Path = ".";
            root.Include = "*.bat";
            root.Recurse = false;
            settings.AddFolderCommand.Execute(null);
            var tools = settings.ScriptFolders[1];
            tools.Path = "tools";
            tools.Include = "*.py *.bat *.ps1";
            tools.Exclude = "test_* *_lib.py";

            var config = Shared(settings, "config");
            config.Editor.DefaultText = "--release";
            AddFields(config.Choices, "dir", "testFlag");
            AddRow(config.Choices, "Debug", "", false, "debug", "");
            AddRow(config.Choices, "Release", "--release", false, "release", "--release");
            AddRow(config.Choices, "Retail", "--final", false, "retail", "");
            AddRow(config.Choices, "Debug ASan", "--asan", false, "debugasan", "--asan");
            AddRow(config.Choices, "Release ASan", "--release --asan", true, "releaseasan", "--release --asan");

            AddLines(Shared(settings, "app").Choices, "build.bat", """set "GAMES=([^"]*)" """.Trim(), " ");
            AddLines(Shared(settings, "webApp").Choices, "CMakeLists.txt", @"add_game_app\((\w+)", "", all: true);
            var game = Shared(settings, "game");
            SetType(game.Editor, ParameterType.Multichoice);
            game.Editor.EmptyMeansAll = true;
            AddLines(game.Choices, "build.bat", """set "GAMES=([^"]*)" """.Trim(), " ", lowercase: true);
            var window = Shared(settings, "window");
            AddRow(window.Choices, "Headless", "");
            AddRow(window.Choices, "Visible", "--show-windows");
            settings.SaveCommand.Execute(null);
        }

        private void BuildFolder()
        {
            NewItem(Node("Workspace/Build"), NewItemKind.Entry, "", "generate.bat");
            NewItem(Node("Workspace/Build"), NewItemKind.Entry, "Build", "build.bat");
            EditScript("Workspace/Build/Build", e =>
            {
                e.General.LockName = "native-build";
                UseShared(e.Parameters, "config").Split = true;
                var apps = UseShared(e.Parameters, "app");
                apps.Name = "apps";
                SetType(apps, ParameterType.Multichoice);
                apps.EmptyMeansAll = true;
                apps.MaxPerCallText = "9";
            });
        }

        private void RunFolder()
        {
            NewItem(Node("Workspace/Run"), NewItemKind.Entry, "Run app", "build/${param:config.dir}/bin/${param:app|lower}.exe");
            EditScript("Workspace/Run/Run app", e =>
            {
                e.General.Runner = e.General.Runners.Single(r => r.Value == Runner.Exe);
                e.General.LongRunning = true;
                UseShared(e.Parameters, "config").Emit = false;
                UseShared(e.Parameters, "app").Emit = false;
            });
            NewWorkflow("Workspace/Run", "Build & run", null, w =>
            {
                w.NameTemplate = "Build ${param:config.label} & run ${param:app}";
                UseShared(w.Parameters, "config");
                UseShared(w.Parameters, "app");
                Bind(AddStep(w, "Workspace/Build/Build"), "apps", "${param:app}");
                AddStep(w, "Workspace/Run/Run app", "run");
            });
        }

        private void WebFolder()
        {
            NewItem(Node("Workspace/Web"), NewItemKind.Entry, "Build web", "build_web.bat");
            EditScript("Workspace/Web/Build web", e => UseShared(e.Parameters, "webApp"));
            NewItem(Node("Workspace/Web"), NewItemKind.Entry, "Serve web build", "serve_web.bat");
            EditScript("Workspace/Web/Serve web build", e =>
            {
                e.Advanced.Id = "serve";
                e.General.LongRunning = true;
                Port(e.Parameters);
                UseShared(e.Parameters, "webApp").Emit = false;
                e.AfterRun.ReadyPattern = @"Press Ctrl\+C to stop";
                e.AfterRun.OpenUrl = "http://localhost:${param:port}/${param:webApp}.html";
            });
            NewItem(Node("Workspace/Web"), NewItemKind.Entry, "Stop web server", "stop_web.bat");
            EditScript("Workspace/Web/Stop web server", e => Port(e.Parameters));
            EditScript("Workspace/Web/Serve web build", e => e.AfterRun.Stop = e.AfterRun.StopChoices.Single(c => c.Value == "stop-web"));
            NewWorkflow("Workspace/Web", "Build web & serve locally", "web-local", w =>
            {
                UseShared(w.Parameters, "webApp");
                AddStep(w, "Workspace/Web/Build web", "build");
                AddStep(w, "Workspace/Web/Serve web build");
            });
            NewItem(Node("Workspace/Web"), NewItemKind.Entry, "Package for web deploy", "package_web.bat");
            EditScript("Workspace/Web/Package for web deploy", e =>
            {
                UseShared(e.Parameters, "webApp");
                Artifact(e.AfterRun, "build/package_web/${param:webApp}");
            });
        }

        private void TestFolder()
        {
            NewWorkflow("Workspace/Test", "Build & run unit tests", "unit-tests", w =>
            {
                UseShared(w.Parameters, "config");
                var suites = Add(w.Parameters, "suites", ParameterType.Multichoice);
                suites.EmptyMeansAll = true;
                AddLines(w.Choices!, "build.bat", """set "TESTS=([^"]*)" """.Trim(), " ");
                var build = Bind(AddStep(w, "Workspace/Build/Build"), "apps", "${param:suites}");
                build.EmptyArgs = "--tests";
            });
            NewItem(Node("Workspace/Test"), NewItemKind.Entry, "Run unit tests", "tools/run_tests.py");
            EditScript("Workspace/Test/Run unit tests", e =>
            {
                Add(e.Parameters, "match", ParameterType.Multichoice).EmptyMeansAll = true;
                Add(e.Parameters, "cfg", ParameterType.Text).Split = true;
                Add(e.Parameters, "gated", ParameterType.Flag).Arg = "--gated";
            });
            EditWorkflow("Workspace/Test/Build & run unit tests", w =>
            {
                var run = AddStep(w, "Workspace/Test/Run unit tests", "run");
                Bind(run, "match", "${param:suites}");
                Bind(run, "cfg", "${param:config.testFlag}");
            });

            NewWorkflow("Workspace/Test", "Build & run benchmarks", "benches", w =>
            {
                Add(w.Parameters, "benches", ParameterType.Multichoice).EmptyMeansAll = true;
                AddLines(w.Choices!, "build.bat", """set "BENCH=([^"]*)" """.Trim(), " ");
                Add(w.Parameters, "tier", ParameterType.Choice);
                AddRow(w.Choices!, "Smoke", "^Smoke");
                AddRow(w.Choices!, "Deep", "^Deep");
                AddRow(w.Choices!, "All", "");
                var build = Bind(AddStep(w, "Workspace/Build/Build"), "apps", "${param:benches}");
                Choose(build.Form!, "config", "Debug");
                Choose(build.Form!, "config", "Release");
                build.EmptyArgs = "--bench";
            });
            NewItem(Node("Workspace/Test"), NewItemKind.Entry, "Run one benchmark", "build/release/benchmarks/${param:bench|lower}.exe");
            EditScript("Workspace/Test/Run one benchmark", e =>
            {
                e.Advanced.Id = "run-bench";
                e.General.Runner = e.General.Runners.Single(r => r.Value == Runner.Exe);
                Add(e.Parameters, "bench", ParameterType.Text).Emit = false;
                Add(e.Parameters, "tier", ParameterType.Text).Arg = "--benchmark_filter=";
            });
            EditWorkflow("Workspace/Test/Build & run benchmarks", w => AddStep(w, "Workspace/Test/Run one benchmark", "each").IsForEach = true);

            NewItem(Node("Workspace/Test"), NewItemKind.Entry, "Run gameplay scenarios");
            EditScript("Workspace/Test/Run gameplay scenarios", e =>
            {
                e.Advanced.Id = "scenarios-run";
                Pytest(e, "--junitxml=${workspaceDir}/build/qa/scenarios.xml");
                e.AfterRun.TestReport = "build/qa/scenarios.xml";
                var files = Add(e.Parameters, "files", ParameterType.Multichoice);
                files.EmptyMeansAll = true;
                files.EmptyArgs = "scenarios";
                e.Choices!.AddSourceCommand.Execute(null);
                var picker = e.Choices.Picker!;
                picker.FolderPath = "tools/qa/scenarios";
                picker.Pattern = "test_*.py";
                picker.ValuesArePaths = true;
                picker.RelativeTo = "tools/qa";
                picker.AcceptCommand.Execute(null);
                var game = UseShared(e.Parameters, "app");
                game.Name = "game";
                game.Arg = "--game";
                game.LowercaseValues = true;
                UseShared(e.Parameters, "window");
                Add(e.Parameters, "sound", ParameterType.Flag).Arg = "--with-sound";
            });
            NewWorkflow("Workspace/Test", "Gameplay scenarios", "scenarios", w =>
            {
                var game = UseShared(w.Parameters, "app");
                game.Name = "game";
                game.LowercaseValues = true;
                UseShared(w.Parameters, "window");
                var build = Bind(AddStep(w, "Workspace/Build/Build"), "apps", "${param:game}");
                Choose(build.Form!, "config", "Debug ASan");
                AddStep(w, "Workspace/Test/Run gameplay scenarios", "run");
            });

            NewItem(Node("Workspace/Test"), NewItemKind.Entry, "QA crawl one game");
            EditScript("Workspace/Test/QA crawl one game", e =>
            {
                e.Advanced.Id = "qa-game";
                Pytest(e, "scenarios/test_crawl.py");
                Add(e.Parameters, "game", ParameterType.Text).Arg = "--game";
                UseShared(e.Parameters, "window");
            });
            NewItem(Node("Workspace/Test"), NewItemKind.Entry, "QA report", "tools/qa/report.py");
            EditScript("Workspace/Test/QA report", e =>
            {
                e.Advanced.Id = "qa-report";
                Artifact(e.AfterRun, "qa_report.html");
            });
            NewWorkflow("Workspace/Test", "QA crawl & use cases", "qa-sweep", w =>
            {
                UseShared(w.Parameters, "game");
                UseShared(w.Parameters, "window");
                var build = Bind(AddStep(w, "Workspace/Build/Build"), "apps", "${param:game}");
                Choose(build.Form!, "config", "Debug ASan");
                build.EmptyArgs = "--games";
                AddStep(w, "Workspace/Test/QA crawl one game", "crawl").IsForEach = true;
                var report = AddStep(w, "Workspace/Test/QA report", "report");
                report.When = report.WhenOptions.Single(o => o.Value == StepWhen.Always);
            });
        }

        private NodeViewModel Node(string automationId) =>
            main.Tree!.Find(automationId)
            ?? throw new AssertFailedException($"No node {automationId}; have {string.Join(", ", main.Tree!.AllNodes.Select(n => n.AutomationId))}");

        private void NewItem(NodeViewModel near, NewItemKind kind, string name, string target = "")
        {
            var command = kind switch
            {
                NewItemKind.Link => main.Tree!.NewLinkCommand,
                NewItemKind.Entry => main.Tree!.NewEntryCommand,
                _ => main.Tree!.NewFolderCommand,
            };
            command.Execute(near);
            var item = main.NewItem!;
            item.Name = name;
            item.Target = target;
            item.SaveCommand.Execute(null);
            Assert.IsNull(main.NewItem, item.Error);
        }

        private void EditScript(string automationId, Action<ScriptEditorViewModel> edit)
        {
            Node(automationId).IsSelected = true;
            main.Details.EditCommand.Execute(null);
            var editor = main.Details.Editor!;
            edit(editor);
            editor.SaveCommand.Execute(null);
            Assert.IsNull(main.Details.Editor, editor.Error);
        }

        private void NewWorkflow(string folder, string name, string? id, Action<WorkflowEditorViewModel> edit)
        {
            main.Tree!.NewWorkflowCommand.Execute(Node(folder));
            var editor = main.Details.WorkflowEditor!;
            editor.Name = name;
            if (id is not null)
                editor.Id = id;
            Save(editor, edit);
        }

        private void EditWorkflow(string automationId, Action<WorkflowEditorViewModel> edit)
        {
            Node(automationId).IsSelected = true;
            main.Details.EditCommand.Execute(null);
            Save(main.Details.WorkflowEditor!, edit);
        }

        private void Save(WorkflowEditorViewModel editor, Action<WorkflowEditorViewModel> edit)
        {
            edit(editor);
            editor.SaveCommand.Execute(null);
            Assert.IsNull(main.Details.WorkflowEditor, editor.Error);
        }

        private StepCardViewModel AddStep(WorkflowEditorViewModel editor, string automationId, string? id = null)
        {
            Assert.IsTrue(editor.AddStep(Node(automationId)));
            var card = editor.Steps.Last();
            if (id is not null)
                card.Id = id;
            return card;
        }

        private static StepCardViewModel Bind(StepCardViewModel card, string parameter, string template)
        {
            card.BindingTarget = parameter;
            card.BindingTemplate = template;
            card.AddBindingCommand.Execute(null);
            return card;
        }

        private static void Choose(ParameterFormViewModel form, string name, string label)
        {
            var field = (ChoiceFieldViewModel)form.Field(name)!;
            field.Selected = field.Options.Single(o => o.Label == label);
        }

        private static ParameterEditorViewModel UseShared(ParametersTabViewModel parameters, string name)
        {
            parameters.AddSharedCommand.Execute(name);
            return parameters.Selected!;
        }

        private static ParameterEditorViewModel Add(ParametersTabViewModel parameters, string name, ParameterType type)
        {
            parameters.AddCommand.Execute(null);
            var parameter = parameters.Selected!;
            parameter.Name = name;
            SetType(parameter, type);
            return parameter;
        }

        private static void SetType(ParameterEditorViewModel parameter, ParameterType type) =>
            parameter.Type = parameter.Types.Single(t => t.Value == type);

        private static void Port(ParametersTabViewModel parameters) => Add(parameters, "port", ParameterType.Int).DefaultText = "8123";

        private static void Pytest(ScriptEditorViewModel editor, string args)
        {
            editor.General.Runner = editor.General.Runners.Single(r => r.Value == Runner.Python);
            editor.General.Module = "pytest";
            editor.General.WorkingDir = "tools/qa";
            editor.Advanced.FixedArgs = args;
        }

        private static void Artifact(AfterRunTabViewModel afterRun, string path)
        {
            afterRun.AddArtifactCommand.Execute(null);
            var artifact = afterRun.Artifacts.Last();
            artifact.Path = path;
            artifact.Open = afterRun.OpenOptions.Single(o => o.Value == ArtifactOpen.OnSuccess);
        }

        private static SharedParameterViewModel Shared(WorkspaceSettingsViewModel settings, string key)
        {
            settings.NewSharedName = key;
            settings.AddSharedCommand.Execute(null);
            return settings.Shared(key)!;
        }

        private static void AddFields(ChoicesTabViewModel choices, params string[] names)
        {
            foreach (var name in names)
            {
                choices.NewFieldName = name;
                choices.AddFieldCommand.Execute(null);
            }
        }

        private static void AddRow(ChoicesTabViewModel choices, string label, string value, bool split = false, params string[] fields)
        {
            choices.AddRowCommand.Execute(null);
            var row = choices.Rows.Last();
            row.Label = label;
            row.Value = value;
            row.Split = split;
            for (var i = 0; i < fields.Length; i++)
                row.Fields[i].Value = fields[i];
        }

        private static void AddLines(ChoicesTabViewModel choices, string file, string regex, string split, bool all = false, bool lowercase = false)
        {
            choices.AddSourceCommand.Execute(null);
            var picker = choices.Picker!;
            picker.SelectKindCommand.Execute(ChoiceSourceKind.Lines);
            picker.FilePath = file;
            picker.RegexText = regex;
            picker.SplitText = split;
            picker.AllMatches = all;
            picker.LowercaseValues = lowercase;
            picker.AcceptCommand.Execute(null);
        }
    }
}
