using System.Text.Json.Nodes;
using BatchPad.Core.Model;
using BatchPad.Core.Output;

namespace BatchPad.Core.Running;

/// <summary>Builds the values that re-run a run's failed tests (§5).</summary>
public static class TestRerun
{
    public static IReadOnlyList<string> FailedNames(JUnitReport report, RerunBy by) =>
        [.. report.Cases.Where(c => c.Outcome == TestOutcome.Failed).Select(c => by == RerunBy.Suite ? c.Suite : c.Name).Distinct()];

    /// <summary>The request's values with the <c>rerunParam</c> set to <paramref name="names"/>: a list for a multichoice, else space-separated.</summary>
    public static Dictionary<string, JsonNode?> Values(RunRequest request, string parameter, IReadOnlyList<string> names)
    {
        var values = request.Values?.ToDictionary(v => v.Key, v => v.Value?.DeepClone()) ?? [];
        var definition = request.Parameters.FirstOrDefault(p => p.Name == parameter);
        values[parameter] = definition?.Type == ParameterType.Multichoice
            ? new JsonArray([.. names.Select(n => JsonValue.Create(n))])
            : JsonValue.Create(string.Join(" ", names));
        return values;
    }
}
