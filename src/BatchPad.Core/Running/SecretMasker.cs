using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Templating;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Running;

/// <summary>Hides <c>secret</c> parameter values in previews, logs and history (§4 "Unattended runs").</summary>
public static class SecretMasker
{
    public const string Placeholder = "••••";

    public static IReadOnlySet<string> SecretNames(RunRequest request) =>
        Secrets(request.Parameters).Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The request's secret values, longest first so one containing another is masked whole.</summary>
    public static IReadOnlyList<string> SecretValues(RunRequest request) =>
        SecretValues(request.Script, request.Workspace, request.Values ?? new Dictionary<string, JsonNode?>(), RunPlanner.TemplatesFor(request));

    /// <summary>
    /// The secret values among <paramref name="values"/>, and the secret parameters' defaults as they expand
    /// (a default such as <c>${env:TOKEN}</c> is as secret as a typed value).
    /// </summary>
    public static IReadOnlyList<string> SecretValues(RunnableNode node, LoadedWorkspace workspace,
        IEnumerable<KeyValuePair<string, JsonNode?>> values, TemplateContext? templates = null)
    {
        var secrets = Secrets(SharedParameters.MergeAll(node.Params, workspace.Workspace.File.SharedParams)).ToList();
        var names = secrets.Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        templates ??= new TemplateContext { WorkspaceDir = workspace.Directory, Variables = workspace.Workspace.File.Variables };
        return [.. values.Where(v => names.Contains(v.Key)).Select(v => v.Value?.ToString())
            .Concat(secrets.Select(p => ExpandedDefault(p, templates)))
            .OfType<string>()
            .Where(s => s.Length > 0)
            .Distinct()
            .OrderByDescending(s => s.Length)];
    }

    public static string Mask(string text, IReadOnlyCollection<string> secrets) =>
        secrets.Aggregate(text, (masked, secret) => masked.Replace(secret, Placeholder, StringComparison.Ordinal));

    private static IEnumerable<ParameterDefinition> Secrets(IEnumerable<ParameterDefinition> parameters) =>
        parameters.Where(p => p.Type == ParameterType.Secret && p.Name is not null);

    private static string? ExpandedDefault(ParameterDefinition parameter, TemplateContext templates)
    {
        if (parameter.Default?.ToString() is not { } text)
            return null;
        try
        {
            return TemplateExpander.ExpandText(text, templates);
        }
        catch (TemplateException)
        {
            return null;
        }
    }
}
