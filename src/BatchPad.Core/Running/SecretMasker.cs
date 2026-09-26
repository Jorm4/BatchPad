using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;

namespace BatchPad.Core.Running;

/// <summary>Hides <c>secret</c> parameter values in previews, logs and history (§4 "Unattended runs").</summary>
public static class SecretMasker
{
    public const string Placeholder = "••••";

    public static IReadOnlySet<string> SecretNames(RunRequest request) =>
        SharedParameters.MergeAll(request.Script.Params, request.Workspace.Workspace.File.SharedParams)
            .Where(p => p.Type == ParameterType.Secret && p.Name is not null)
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The request's secret values, longest first so one containing another is masked whole.</summary>
    public static IReadOnlyList<string> SecretValues(RunRequest request)
    {
        var names = SecretNames(request);
        return [.. (request.Values ?? new Dictionary<string, JsonNode?>())
            .Where(v => names.Contains(v.Key) && v.Value?.ToString() is { Length: > 0 })
            .Select(v => v.Value!.ToString())
            .Distinct()
            .OrderByDescending(s => s.Length)];
    }

    public static string Mask(string text, IReadOnlyCollection<string> secrets) =>
        secrets.Aggregate(text, (masked, secret) => masked.Replace(secret, Placeholder, StringComparison.Ordinal));
}
