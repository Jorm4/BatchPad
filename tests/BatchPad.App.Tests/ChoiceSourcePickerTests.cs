using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Editor;
using BatchPad.Core.Model;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class ChoiceSourcePickerTests
{
    [TestMethod]
    public void WordsAfterEqualsOnLineTwoGiveThreeChoicesThatSurviveASave()
    {
        using var test = new TestWorkspace();
        var (main, editor) = EditGameChoices(test);
        var choices = editor.Choices!;

        choices.AddSourceCommand.Execute(null);
        var picker = choices.Picker!;
        picker.SelectKindCommand.Execute(ChoiceSourceKind.Lines);
        picker.FilePath = "games.bat";
        picker.SelectLine(picker.Line(2)!);

        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, picker.Preview.Select(p => p.Value).ToList());
        Assert.IsTrue(picker.Line(2)!.IsMatch);
        Assert.IsFalse(picker.Line(1)!.IsMatch);
        var source = picker.BuildSource()!;
        Assert.AreEqual("games.bat", source.File);
        Assert.AreEqual(" ", source.Split);

        picker.AcceptCommand.Execute(null);
        Assert.IsNull(choices.Picker);
        choices.RemoveRowCommand.Execute(choices.Rows.Single());
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, choices.Preview.Select(p => p.Value).ToList());
        editor.SaveCommand.Execute(null);

        var reopened = Edit(main, "Workspace/Games");
        reopened.Parameters.Selected = reopened.Parameters.Field("game");
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, reopened.Choices!.Preview.Select(p => p.Value).ToList());
        Assert.HasCount(1, reopened.Choices.Sources);
    }

    [TestMethod]
    public void TheLowercaseTransformKeepsTheLabelAndLowersTheValue()
    {
        using var test = new TestWorkspace();
        var (_, editor) = EditGameChoices(test);
        editor.Choices!.AddSourceCommand.Execute(null);
        var picker = editor.Choices.Picker!;
        picker.SelectKindCommand.Execute(ChoiceSourceKind.Lines);
        picker.FilePath = "games.bat";
        picker.SelectLine(picker.Line(2)!);

        picker.LowercaseValues = true;

        var first = picker.Preview.First();
        Assert.AreEqual("A", first.Label);
        Assert.AreEqual("a", first.Value);
        Assert.AreEqual("lower", picker.BuildSource()!.ValueTransform);
    }

    [TestMethod]
    public void FilesInAFolderAreNamedByTheClickedPathSegment()
    {
        using var test = new TestWorkspace();
        var (_, editor) = EditGameChoices(test);
        editor.Choices!.AddSourceCommand.Execute(null);
        var picker = editor.Choices.Picker!;

        picker.FolderPath = "apps";
        picker.Pattern = "*/qa/cases.md";
        CollectionAssert.AreEqual(new[] { "cases", }, picker.Preview.Select(p => p.Value).ToList());
        picker.SelectSegment(picker.Segments[1]);

        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, picker.Preview.Select(p => p.Value).ToList());
        Assert.AreEqual("^apps/([^/]+)/", picker.BuildSource()!.Match);
    }

    [TestMethod]
    public void AFixedListReplacesTheRowsWithLabelsAndValues()
    {
        using var test = new TestWorkspace();
        var (_, editor) = EditGameChoices(test);
        editor.Choices!.AddSourceCommand.Execute(null);
        var picker = editor.Choices.Picker!;
        picker.SelectKindCommand.Execute(ChoiceSourceKind.Fixed);
        picker.AddFixedRowCommand.Execute(null);
        picker.FixedRows[0].Label = "robots";
        picker.FixedRows[0].Value = "robotmanager";

        Assert.AreEqual(new ChoicePreviewRow("robots", "robotmanager"), picker.Preview.Single());
        picker.AcceptCommand.Execute(null);

        var choice = editor.Parameters.Field("game")!.Definition.Choices!.Single();
        Assert.AreEqual("robotmanager", choice.Value);
        Assert.AreEqual("robots", choice.Label);
    }

    [TestMethod]
    public void ExampleValueDerivesAPatternForEveryName()
    {
        using var test = new TestWorkspace();
        var (_, editor) = EditGameChoices(test);
        editor.Choices!.AddSourceCommand.Execute(null);
        var picker = editor.Choices.Picker!;
        picker.SelectKindCommand.Execute(ChoiceSourceKind.Lines);
        picker.FilePath = "apps.cmake";
        picker.SelectLine(picker.Line(2)!);

        picker.ExampleValue = "Beta";

        CollectionAssert.AreEqual(new[] { "Alpha", "Beta", "Gamma" }, picker.Preview.Select(p => p.Value).ToList());
        Assert.IsTrue(picker.Line(1)!.IsMatch);
        Assert.IsFalse(picker.Line(4)!.IsMatch);
    }

    [TestMethod]
    public void ScriptOutputSourceListsItsLinesAndSurvivesASave()
    {
        using var test = new TestWorkspace();
        var (main, editor) = EditGameChoices(test);
        editor.Choices!.AddSourceCommand.Execute(null);
        var picker = editor.Choices.Picker!;

        picker.SelectKindCommand.Execute(ChoiceSourceKind.Script);
        picker.ScriptPath = "list_games.bat";

        CollectionAssert.AreEqual(new[] { "Red", "Blue" }, picker.Preview.Select(p => p.Value).ToList());
        picker.AcceptCommand.Execute(null);
        editor.SaveCommand.Execute(null);
        var reopened = Edit(main, "Workspace/Games");
        reopened.Parameters.Selected = reopened.Parameters.Field("game");
        Assert.AreEqual("list_games.bat", reopened.Choices!.Sources.Single().Source.Command);
    }

    private static (MainViewModel, ScriptEditorViewModel) EditGameChoices(TestWorkspace test)
    {
        var demo = test.CopyDemo();
        File.WriteAllText(Path.Combine(demo, "games.bat"), "@echo off\r\nset \"GAMES=A B C\"\r\necho %GAMES%\r\n");
        File.WriteAllText(Path.Combine(demo, "apps.cmake"),
            "add_app(Alpha src/a)\nadd_app(Beta src/b)\nadd_app(Gamma src/c)\nadd_library(common src/common)\n");
        File.WriteAllText(Path.Combine(demo, "list_games.bat"), "@echo off\r\necho Red\r\necho Blue\r\n");
        foreach (var app in new[] { "alpha", "beta" })
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(demo, "apps", app, "qa")).FullName, "cases.md"), "");
        Core.Config.ConfigWriter.Update(Path.Combine(demo, "batchpad.json"), f => f.Scripts.Add(new ScriptNode
        {
            Id = "games", Name = "Games", Path = "games.bat",
            Params = [new ParameterDefinition { Name = "game", Type = ParameterType.Choice, Choices = [new ChoiceDefinition { Value = "Z" }] }],
        }));
        var main = test.OpenMain(demo, trusted: true);
        var editor = Edit(main, "Workspace/Games");
        editor.Parameters.Selected = editor.Parameters.Field("game");
        return (main, editor);
    }

    private static ScriptEditorViewModel Edit(MainViewModel main, string automationId)
    {
        main.Select(automationId);
        main.Details.EditCommand.Execute(null);
        return main.Details.Editor!;
    }
}
