using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class ParameterFormTests
{
    private const string ParamsDemo = "Workspace/Parameters demo";

    [TestMethod]
    public void EveryTypeGetsItsField()
    {
        using var test = new TestWorkspace();
        var form = Select(test).Details.Form!;

        Assert.IsTrue(((ChoiceFieldViewModel)form.Field("config")!).IsSegmented);
        Assert.IsFalse(((ChoiceFieldViewModel)form.Field("game")!).IsSegmented);
        Assert.IsInstanceOfType<MultichoiceFieldViewModel>(form.Field("tests"));
        Assert.IsInstanceOfType<FlagFieldViewModel>(form.Field("verbose"));
        Assert.IsInstanceOfType<IntFieldViewModel>(form.Field("port"));
        Assert.IsInstanceOfType<TextFieldViewModel>(form.Field("targets"));
        Assert.IsTrue(((PathFieldViewModel)form.Field("out")!).IsFolder);
    }

    [TestMethod]
    public void PickingReleaseAddsItToThePreview()
    {
        using var test = new TestWorkspace();
        var details = Select(test).Details;
        var config = (ChoiceFieldViewModel)details.Form!.Field("config")!;
        Assert.AreEqual("Debug", config.Selected!.Label);
        Assert.DoesNotContain("--release", details.Preview);

        config.Selected = config.Options.Single(o => o.Label == "Release");

        Assert.Contains("--release", details.Preview);
    }

    [TestMethod]
    public void AnEmptyMultichoiceMeansAllAndEmitsNothing()
    {
        using var test = new TestWorkspace();
        var details = Select(test).Details;
        var tests = (MultichoiceFieldViewModel)details.Form!.Field("tests")!;

        Assert.AreEqual("All", tests.Summary);
        Assert.AreEqual("Clear = all", tests.ClearLabel);
        Assert.DoesNotContain("--test", details.Preview);

        tests.Items.Single(i => i.Label == "Audio").IsChecked = true;
        tests.Items.Single(i => i.Label == "Physics").IsChecked = true;

        Assert.AreEqual("Physics, Audio", tests.Summary);
        Assert.Contains("--test Physics --test Audio", details.Preview);

        tests.Filter = "ph";
        CollectionAssert.AreEqual(new[] { "Physics" }, tests.Items.Where(i => i.IsVisible).Select(i => i.Label).ToList());

        tests.ClearCommand.Execute(null);
        Assert.AreEqual("All", tests.Summary);
        Assert.DoesNotContain("--test", details.Preview);
    }

    [TestMethod]
    public void AnOutOfRangeIntBlocksRunWithAMessage()
    {
        using var test = new TestWorkspace();
        var details = Select(test).Details;
        var port = (IntFieldViewModel)details.Form!.Field("port")!;
        Assert.AreEqual("8123", port.Text);
        Assert.IsTrue(details.RunCommand.CanExecute(null));

        port.Text = "70000";

        Assert.AreEqual("Must be between 1 and 65535.", port.Error);
        Assert.AreEqual("Port: Must be between 1 and 65535.", details.ValidationMessage);
        Assert.IsFalse(details.RunCommand.CanExecute(null));

        port.Text = "9000";

        Assert.IsNull(details.ValidationMessage);
        Assert.IsTrue(details.RunCommand.CanExecute(null));
        Assert.Contains("--port 9000", details.Preview);
    }

    [TestMethod]
    public void ValuesAndExtraArgumentsSurviveSwitchingAwayAndBack()
    {
        using var test = new TestWorkspace();
        var main = Select(test);
        ((FlagFieldViewModel)main.Details.Form!.Field("verbose")!).IsChecked = true;
        ((TextFieldViewModel)main.Details.Form.Field("targets")!).Text = "Alpha Beta";
        main.Details.Form.ExtraArguments = "--dry-run";

        main.Tree!.Find("Workspace/Hello/hello.bat")!.IsSelected = true;
        Assert.DoesNotContain("--verbose", main.Details.Preview);
        main.Tree.Find(ParamsDemo)!.IsSelected = true;

        Assert.IsTrue(((FlagFieldViewModel)main.Details.Form!.Field("verbose")!).IsChecked);
        Assert.AreEqual("--dry-run", main.Details.Form.ExtraArguments);
        StringAssert.EndsWith(main.Details.Preview, "--verbose --port 8123 Alpha Beta --dry-run");
    }

    private static MainViewModel Select(TestWorkspace test)
    {
        var main = new MainViewModel(test.Paths, new Settings(), new FakeLauncher(), shell: new FakeShell());
        main.Trust.Trust(TestWorkspace.DemoSource);
        main.Open(Path.Combine(TestWorkspace.DemoSource, "batchpad.json"));
        main.Tree!.Find(ParamsDemo)!.IsSelected = true;
        return main;
    }
}
