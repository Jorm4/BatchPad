using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Editor;
using BatchPad.Core.Config;
using BatchPad.Core.Model;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class EditorTabsTests
{
    [TestMethod]
    public void EnvironmentVariableAndEnvFileRoundTrip()
    {
        using var test = new TestWorkspace();
        var (main, demo) = Open(test);
        var editor = Edit(main);

        editor.Environment.AddVariableCommand.Execute(null);
        editor.Environment.Variables.Single().Key = "FOO";
        editor.Environment.Variables.Single().Value = "bar";
        editor.Environment.EnvFile = ".env";
        editor.SaveCommand.Execute(null);

        var saved = Read(demo);
        Assert.AreEqual("bar", saved.Env!["FOO"]);
        Assert.AreEqual(".env", saved.EnvFile);
        var reopened = Edit(main);
        Assert.AreEqual("FOO", reopened.Environment.Variables.Single().Key);
        Assert.AreEqual("bar", reopened.Environment.Variables.Single().Value);
        Assert.AreEqual(".env", reopened.Environment.EnvFile);
    }

    [TestMethod]
    public void ReadyTesterReportsTheMatchAndExpandedUrl()
    {
        using var test = new TestWorkspace();
        var (main, _) = Open(test);
        var afterRun = Edit(main).AfterRun;

        afterRun.ReadyPattern = @"Serving on (http://\S+)";
        afterRun.OpenUrl = "$1/index.html";
        afterRun.SampleOutput = "starting\r\nServing on http://localhost:8000\r\n";
        StringAssert.Contains(afterRun.TesterResult, "Opens: http://localhost:8000/index.html");

        afterRun.SampleOutput = "starting\r\nstill starting";
        Assert.AreEqual("No match", afterRun.TesterResult);
    }

    [TestMethod]
    public void ErrorPatternsAndArgsTemplateRoundTripAndPreview()
    {
        using var test = new TestWorkspace();
        var (main, demo) = Open(test);
        var editor = Edit(main);

        editor.AfterRun.ErrorPatterns = "LNK\\d{4}\r\nerror C\\d+";
        editor.Advanced.ArgsTemplate = "--first last";
        StringAssert.Contains(editor.Advanced.Preview, "hello.bat\" --first last");
        editor.SaveCommand.Execute(null);

        var saved = Read(demo);
        CollectionAssert.AreEqual(new[] { "LNK\\d{4}", "error C\\d+" }, saved.ErrorPatterns);
        CollectionAssert.AreEqual(new[] { "--first", "last" }, saved.ArgsTemplate);
        var reopened = Edit(main);
        Assert.AreEqual("--first last", reopened.Advanced.ArgsTemplate);
        StringAssert.Contains(reopened.AfterRun.ErrorPatterns, "error C\\d+");
    }

    [TestMethod]
    public void RerunParameterAndScopeRoundTrip()
    {
        using var test = new TestWorkspace();
        var (main, demo) = Open(test);
        var editor = Edit(main);

        editor.Parameters.AddCommand.Execute(null);
        var afterRun = editor.AfterRun;
        afterRun.TestReport = "out/junit.xml";
        afterRun.RerunParam = afterRun.RerunParamChoices.Single(c => c.Value == "param1");
        afterRun.RerunScope = afterRun.RerunByChoices.Single(c => c.Value == RerunBy.Suite);
        editor.SaveCommand.Execute(null);

        var report = Read(demo).TestReport!;
        Assert.AreEqual("out/junit.xml", report.Path);
        Assert.AreEqual("param1", report.RerunParam);
        Assert.AreEqual(RerunBy.Suite, report.RerunBy);
        var reopened = Edit(main).AfterRun;
        Assert.AreEqual("param1", reopened.RerunParam.Value);
        Assert.AreEqual(RerunBy.Suite, reopened.RerunScope.Value);

        reopened.RerunParam = reopened.RerunParamChoices[0];
        reopened.RerunScope = reopened.RerunByChoices[0];
        Assert.IsTrue(main.Details.Editor!.Definition.TestReport!.IsPlain);
    }

    private static (MainViewModel, string) Open(TestWorkspace test)
    {
        var demo = test.CopyDemo();
        return (test.OpenMain(demo, trusted: true), demo);
    }

    private static ScriptEditorViewModel Edit(MainViewModel main)
    {
        main.Tree!.AllNodes.Single(n => n.Node is ScriptNode { Id: "hello-bat" }).IsSelected = true;
        main.Details.EditCommand.Execute(null);
        return main.Details.Editor!;
    }

    private static ScriptNode Read(string demo) =>
        ConfigReader.ReadFile(Path.Combine(demo, "batchpad.json")).Scripts
            .SelectMany(n => n is FolderNode f ? f.Items : [n]).OfType<ScriptNode>().Single(s => s.Id == "hello-bat");
}
