using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Output;
using BatchPad.Core.Templating;

namespace BatchPad.Core.Running;

/// <summary>Finds a run's JUnit report and builds the values that re-run its failed tests (§5).</summary>
public static class TestRerun
{
    /// <exception cref="TemplateException">The path uses a variable that has no value.</exception>
    public static string? ReportPath(RunRequest request) =>
        request.Script.TestReport?.Path is { Length: > 0 } path
            ? Path.GetFullPath(Path.Combine(request.Tree.BaseDirectory, TemplateExpander.ExpandText(path, RunPlanner.BoundTemplatesFor(request))))
            : null;

    public static IReadOnlyList<string> FailedNames(JUnitReport report, RerunBy by) =>
        [.. report.Cases.Where(c => c.Outcome == TestOutcome.Failed).Select(c => by == RerunBy.Suite ? c.Suite : c.Name).Distinct()];

    /// <summary>The request's values with the <c>rerunParam</c> set to <paramref name="names"/>: a list for a multichoice, else space-separated.</summary>
    public static Dictionary<string, JsonNode?> Values(RunRequest request, string parameter, IReadOnlyList<string> names)
    {
        var values = request.Values?.ToDictionary(v => v.Key, v => v.Value?.DeepClone()) ?? [];
        var definition = SharedParameters.MergeAll(request.Script.Params, request.Workspace.Workspace.File.SharedParams)
            .FirstOrDefault(p => p.Name == parameter);
        values[parameter] = definition?.Type == ParameterType.Multichoice
            ? new JsonArray([.. names.Select(n => JsonValue.Create(n))])
            : JsonValue.Create(string.Join(" ", names));
        return values;
    }
}
