using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.History;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class AskOnRunTests
{
    [TestMethod]
    public async Task TheAskPickerDefaultsToTheLastRecordedValue()
    {
        using var test = new TestWorkspace();
        var (main, launcher, ask) = Open(test);
        main.Select("Workspace/Pick");
        Assert.IsNull(main.Details.Form!.Field("app"));

        ask.Answer = form => Choose(form, "app", "game");
        main.Details.RunCommand.Execute(null);
        Assert.AreEqual("game", launcher.Requests.Single().Values!["app"]!.GetValue<string>());
        await FinishAndRecord(main, launcher.Started.Single());

        ParameterFormViewModel? asked = null;
        ask.Answer = form => asked = form;
        main.Details.RunCommand.Execute(null);

        Assert.AreEqual("game", ((ChoiceFieldViewModel)asked!.Field("app")!).Selected!.Value);
        Assert.IsFalse(asked.HasExtraArguments);
        Assert.AreEqual("game", launcher.Requests[1].Values!["app"]!.GetValue<string>());
    }

    [TestMethod]
    public void CancellingTheAskPickerRunsNothing()
    {
        using var test = new TestWorkspace();
        var (main, launcher, ask) = Open(test);
        main.Select("Workspace/Pick");

        ask.Result = false;
        main.Details.RunCommand.Execute(null);

        Assert.IsEmpty(launcher.Requests);
        Assert.IsEmpty(main.Output.Tabs);
    }

    [TestMethod]
    public async Task ASecretNeverAppearsInThePreviewHistoryLogOrUserJson()
    {
        using var test = new TestWorkspace();
        var (main, launcher, ask) = Open(test);
        main.Select("Workspace/Login");
        var secret = (SecretFieldViewModel)main.Details.Form!.Field("token")!;
        secret.Text = "hunter2";

        Assert.DoesNotContain("hunter2", main.Details.Preview);
        StringAssert.Contains(main.Details.Preview, SecretMasker.Placeholder);

        main.Details.RunCommand.Execute(null);
        Assert.AreEqual(0, ask.Asked);
        Assert.AreEqual("hunter2", launcher.Requests.Single().Values!["token"]!.GetValue<string>());
        var process = launcher.Started.Single();
        process.Emit("signed in with hunter2", OutputStream.Stdout);
        var run = (RunViewModel)main.Output.Tabs.Single();
        Assert.AreEqual($"signed in with {SecretMasker.Placeholder}", run.Lines.Single().Text);
        var record = await FinishAndRecord(main, process);

        Assert.AreEqual(RunRecord.Masked, record.Values["token"]!.GetValue<string>());
        var store = main.History.Store!;
        Assert.DoesNotContain("hunter2", File.ReadAllText(store.LogPath(record)));
        Assert.DoesNotContain("hunter2", File.ReadAllText(Path.Combine(store.Directory, record.Id + ".json")));

        main.Details.SaveAsMyScriptCommand.Execute(null);
        ((SecretFieldViewModel)main.Details.Form!.Field("token")!).Text = "hunter2";
        var userFile = main.Paths.UserFile(main.Workspace!.Id);
        Assert.IsTrue(File.Exists(userFile));
        Assert.DoesNotContain("hunter2", File.ReadAllText(userFile));
    }

    [TestMethod]
    public void AnEmptySecretIsAskedForWhenTheRunStarts()
    {
        using var test = new TestWorkspace();
        var (main, launcher, ask) = Open(test);
        main.Select("Workspace/Login");

        ask.Answer = form => ((SecretFieldViewModel)form.Field("token")!).Text = "s3cret";
        main.Details.RunCommand.Execute(null);

        Assert.AreEqual(1, ask.Asked);
        Assert.AreEqual("s3cret", launcher.Requests.Single().Values!["token"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task AnAskedSecretIsNotPrefilledWithTheMaskFromHistory()
    {
        using var test = new TestWorkspace();
        var (main, launcher, ask) = Open(test);
        main.Select("Workspace/Login");
        ask.Answer = form => ((SecretFieldViewModel)form.Field("token")!).Text = "s3cret";
        main.Details.RunCommand.Execute(null);
        await FinishAndRecord(main, launcher.Started.Single());

        string? prefilled = null;
        ask.Answer = form => prefilled = ((SecretFieldViewModel)form.Field("token")!).Text;
        main.Details.RunCommand.Execute(null);

        Assert.AreEqual("", prefilled);
    }

    private static (MainViewModel, FakeLauncher, FakeAsk) Open(TestWorkspace test)
    {
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "ws")).FullName;
        File.WriteAllText(Path.Combine(directory, "pick.bat"), "@echo %1\r\n");
        File.WriteAllText(Path.Combine(directory, "login.bat"), "@echo %1\r\n");
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
            { "id": "ask-test", "scripts": [
              { "id": "pick", "name": "Pick", "path": "pick.bat",
                "params": [ { "name": "app", "type": "choice", "choices": ["editor", "game"], "ask": true } ] },
              { "id": "login", "name": "Login", "path": "login.bat", "params": [ { "name": "token", "type": "secret" } ] } ] }
            """);
        var launcher = new FakeLauncher();
        var ask = new FakeAsk();
        var main = test.OpenMain(directory, trusted: true, launcher: launcher, shell: new FakeShell(), ask: ask);
        return (main, launcher, ask);
    }

    private static void Choose(ParameterFormViewModel form, string name, string value)
    {
        var field = (ChoiceFieldViewModel)form.Field(name)!;
        field.Selected = field.Options.Single(o => o.Value == value);
    }

    private static async Task<RunRecord> FinishAndRecord(MainViewModel main, FakeProcess process)
    {
        var recorded = new TaskCompletionSource<RunRecord>();
        main.History.Store!.RunRecorded += record => recorded.TrySetResult(record);
        process.Finish(RunOutcome.Exited, 0);
        return await recorded.Task.WaitAsync(Limit);
    }

    private sealed class FakeAsk : IAskService
    {
        public Action<ParameterFormViewModel>? Answer { get; set; }
        public bool Result { get; set; } = true;
        public int Asked { get; private set; }

        public bool Ask(string title, ParameterFormViewModel form)
        {
            Asked++;
            Answer?.Invoke(form);
            return Result;
        }
    }
}
