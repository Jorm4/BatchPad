using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Running;

/// <summary>Fills an unattended run's missing <c>secret</c> values from the secret store; <c>ask</c> values are not stored.</summary>
public static class SecretFill
{
    public static RunRequest Apply(RunRequest request, ISecretStore store) =>
        request.Unattended && Fill(request.Script, request.Workspace, request.Tree.Kind, request.Values, store) is { } values
            ? request with { Values = values }
            : request;

    public static WorkflowRequest Apply(WorkflowRequest request, LoadedWorkspace workspace, ISecretStore store) =>
        request.Unattended && Fill(request.Workflow, workspace, request.Tree.Kind, request.Values, store) is { } values
            ? request with { Values = values }
            : request;

    /// <summary>Each secret parameter and the name it is stored under, which for <c>use</c> is the shared parameter's.</summary>
    public static IEnumerable<(string Name, string StoredAs)> SecretParameters(RunnableNode node, LoadedWorkspace workspace)
    {
        var shared = workspace.Workspace.File.SharedParams;
        foreach (var parameter in node.Params ?? [])
        {
            ParameterDefinition merged;
            try
            {
                merged = SharedParameters.Merge(parameter, shared);
            }
            catch (ArgumentAssemblyException)
            {
                continue;
            }
            if (merged is { Type: ParameterType.Secret, Name: { } name })
                yield return (name, parameter.Use ?? name);
        }
    }

    private static Dictionary<string, JsonNode?>? Fill(RunnableNode node, LoadedWorkspace workspace, TreeKind tree,
        IReadOnlyDictionary<string, JsonNode?>? values, ISecretStore store)
    {
        Dictionary<string, JsonNode?>? filled = null;
        SecretScope? scope = null;
        foreach (var (name, storedAs) in SecretParameters(node, workspace))
        {
            if (values?.GetValueOrDefault(name) is not null || (scope ??= SecretScope.Of(workspace, tree)).Get(store, storedAs) is not { } secret)
                continue;
            filled ??= values is null ? [] : new Dictionary<string, JsonNode?>(values);
            filled[name] = JsonValue.Create(secret);
        }
        return filled;
    }
}
