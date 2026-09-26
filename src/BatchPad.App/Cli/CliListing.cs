using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Cli;

public sealed record CliListEntry(string Id, string Name, string Kind, string? Folder, string? Description, List<CliParameter> Parameters);

/// <param name="DynamicChoices">The choices also come from files or a command, so <paramref name="Choices"/> may be incomplete.</param>
public sealed record CliParameter(string Name, string? Label, ParameterType? Type, string? Description, bool Required, JsonNode? Default,
    List<CliChoice>? Choices, bool DynamicChoices);

public sealed record CliChoice(string Value, string Label);

internal static class CliListing
{
    public static CliListEntry Describe(CliRunner.Entry entry, LoadedWorkspace workspace) => new(
        entry.Reference,
        entry.Name,
        entry.Definition is WorkflowNode ? "workflow" : "script",
        entry.Folder,
        entry.Description,
        [.. (entry.Definition.Params ?? []).Select(p => Merged(p, workspace)).OfType<ParameterDefinition>()
            .Where(p => p.Name is not null)
            .Select(p => Describe(p, entry.Values?.GetValueOrDefault(p.Name!)))]);

    private static ParameterDefinition? Merged(ParameterDefinition parameter, LoadedWorkspace workspace)
    {
        try
        {
            return SharedParameters.Merge(parameter, workspace.Workspace.File.SharedParams);
        }
        catch (ArgumentAssemblyException)
        {
            return null;
        }
    }

    private static CliParameter Describe(ParameterDefinition parameter, JsonNode? customised) => new(
        parameter.Name!,
        parameter.Label,
        parameter.Type,
        parameter.Description,
        parameter.Required == true,
        (customised ?? parameter.Default)?.DeepClone(),
        parameter.Choices?.Select(c => new CliChoice(c.Value, c.DisplayLabel)).ToList(),
        parameter.ChoicesFrom is { Count: > 0 });
}
