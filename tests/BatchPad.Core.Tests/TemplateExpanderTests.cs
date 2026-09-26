using System.Text.Json;
using BatchPad.Core.Model;
using BatchPad.Core.Templating;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class TemplateExpanderTests
{
    private static readonly ChoiceDefinition Release = new()
    {
        Value = "--release",
        Label = "Release",
        Fields = { ["dir"] = JsonDocument.Parse("\"release\"").RootElement },
    };

    private static readonly TemplateContext Context = new()
    {
        WorkspaceDir = @"C:\repo",
        ScriptDir = @"C:\repo\tools",
        Variables = new Dictionary<string, string> { ["webDir"] = "${workspaceDir}/build/web", ["loop"] = "${loop}" },
        Params = new Dictionary<string, TemplateValue>
        {
            ["config"] = TemplateValue.OfChoice(Release),
            ["app"] = TemplateValue.Of("RobotManager"),
            ["apps"] = TemplateValue.OfList(["Alpha", "Beta"]),
        },
        Item = TemplateValue.OfChoice(Release),
        Workflow = new Dictionary<string, string> { ["name"] = "Nightly", ["result"] = "success" },
        Steps = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["build"] = new Dictionary<string, string> { ["exitCode"] = "0", ["url"] = "http://x" },
        },
        Environment = name => name == "BP_TEST" ? "from-env" : null,
    };

    private static string Text(string template) => TemplateExpander.ExpandText(template, Context);

    [TestMethod]
    [DataRow("${workspaceDir}/x", @"C:\repo/x")]
    [DataRow("${scriptDir}", @"C:\repo\tools")]
    [DataRow("${env:BP_TEST}", "from-env")]
    [DataRow("${BP_TEST}", "from-env")]
    [DataRow("${param:app}.exe", "RobotManager.exe")]
    [DataRow("${param:config}", "--release")]
    [DataRow("${param:config.label}", "Release")]
    [DataRow("build/${param:config.dir}/bin", "build/release/bin")]
    [DataRow("${webDir}", @"C:\repo/build/web")]
    [DataRow("${item.label}:${item}", "Release:--release")]
    [DataRow("${workflow.name}: ${workflow.result}", "Nightly: success")]
    [DataRow("${steps.build.exitCode} ${steps.build.url}", "0 http://x")]
    [DataRow("${param:app|lower}", "robotmanager")]
    [DataRow("${param:app | upper}", "ROBOTMANAGER")]
    [DataRow("${param:app|lower|quote}", "\"robotmanager\"")]
    [DataRow("no variables", "no variables")]
    public void ExpandsEachVariableKindAndFilter(string template, string expected) =>
        Assert.AreEqual(expected, Text(template));

    [TestMethod]
    public void BareVariableKeepsItsTypeAndEmbeddedOneIsJoined()
    {
        var bare = TemplateExpander.Expand("${param:apps}", Context);
        Assert.IsTrue(bare.IsList);
        CollectionAssert.AreEqual(new[] { "Alpha", "Beta" }, bare.Items!.ToList());

        var filtered = TemplateExpander.Expand("${param:apps|lower}", Context);
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, filtered.Items!.ToList());

        var embedded = TemplateExpander.Expand("x ${param:apps}", Context);
        Assert.IsFalse(embedded.IsList);
        Assert.AreEqual("x Alpha Beta", embedded.Text);
    }

    [TestMethod]
    [DataRow("${param:nope}", "param:nope")]
    [DataRow("a ${missing} b", "missing")]
    [DataRow("${env:BP_MISSING}", "env:BP_MISSING")]
    [DataRow("${param:config.nope}", "param:config.nope")]
    [DataRow("${steps.other.exitCode}", "steps.other.exitCode")]
    [DataRow("${loop}", "loop")]
    [DataRow("${param:app|shout}", "param:app")]
    public void UnknownVariableIsAnErrorNamingIt(string template, string variable)
    {
        var error = Assert.ThrowsExactly<TemplateException>(() => Text(template));
        Assert.AreEqual(variable, error.Variable);
    }
}
