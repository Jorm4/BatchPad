using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Customisation;

/// <summary>Turns My Scripts entries into shared ones and back (§5.1 "Promoting and demoting").</summary>
public static class Flattener
{
    /// <summary>
    /// A My Scripts entry as a real entry of <paramref name="target"/>: a customisation's values become parameter defaults
    /// and its extra arguments fixed <c>args</c>; a standalone entry is copied as is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry's base is missing.</exception>
    public static RunnableNode ToShared(ResolvedCustomisation resolved, ScriptTree target, IReadOnlySet<string> takenIds)
    {
        if (resolved is not { Definition: { } definition, DefinitionTree: { } from })
            throw new InvalidOperationException(resolved.Problem ?? "The entry has no definition.");
        var shared = CustomisationResolver.Clone<TreeNode>(definition) as RunnableNode
            ?? throw new InvalidOperationException("Clone lost the node type.");
        shared.Name = resolved.Name;
        shared.Id = IdAssigner.FromName(resolved.Name, takenIds);
        shared.Hidden = null;
        ApplyDefaults(shared.Params, resolved.Values);

        if (shared is ScriptNode script)
        {
            if (resolved.ExtraArgs is { Length: > 0 } extra && extra.Trim().Length > 0)
                script.Args = [.. script.Args ?? [], .. ArgumentAssembler.SplitArguments(extra)];
            script.Base = null;
            script.Values = null;
            script.ExtraArgs = null;
            script.StepValues = null;
            Rebase(script, from, target);
        }
        else if (shared is WorkflowNode workflow)
        {
            foreach (var step in workflow.Steps)
            {
                if (step.Id is { } id && resolved.StepValues.TryGetValue(id, out var values))
                {
                    step.Values ??= [];
                    foreach (var (name, value) in values)
                        step.Values[name] = value?.DeepClone();
                }
                if (from.Scope != target.Scope && step.Run is { } run)
                    step.Run = Qualify(run, from);
            }
        }
        return shared;
    }

    /// <summary>A standalone My Scripts copy of a shared script: paths made absolute and references qualified, so it runs the same command.</summary>
    public static ScriptNode ToStandalone(ScriptNode shared, ScriptTree from, string name)
    {
        var copy = CustomisationResolver.Clone(shared);
        copy.Id = null;
        copy.Hidden = null;
        copy.Name = name;
        copy.Path = Absolute(copy.Path, from.BaseDirectory);
        copy.WorkingDir = Absolute(copy.WorkingDir, from.BaseDirectory);
        copy.EnvFile = Absolute(copy.EnvFile, from.BaseDirectory);
        QualifyReferences(copy, from);
        return copy;
    }

    private static void ApplyDefaults(List<ParameterDefinition>? parameters, IReadOnlyDictionary<string, JsonNode?> values)
    {
        foreach (var parameter in parameters ?? [])
            if ((parameter.Name ?? parameter.Use) is { } name && values.TryGetValue(name, out var value))
                parameter.Default = value?.DeepClone();
    }

    private static void Rebase(ScriptNode script, ScriptTree from, ScriptTree target)
    {
        if (!string.Equals(from.BaseDirectory, target.BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            script.Path = Relative(script.Path, from.BaseDirectory, target.BaseDirectory);
            script.WorkingDir = Relative(script.WorkingDir, from.BaseDirectory, target.BaseDirectory);
            script.EnvFile = Relative(script.EnvFile, from.BaseDirectory, target.BaseDirectory);
        }
        if (from.Scope != target.Scope)
            QualifyReferences(script, from);
    }

    private static void QualifyReferences(ScriptNode script, ScriptTree from)
    {
        if (script.Stop is { } stop)
            script.Stop = Qualify(stop, from);
        if (script.DependsOn is { } dependsOn)
            script.DependsOn = [.. dependsOn.Select(d => Qualify(d, from))];
    }

    private static string Qualify(string reference, ScriptTree from) =>
        reference.Contains(':') ? reference : ReferenceResolver.Qualified(from, reference);

    private static bool IsPlainRelative(string? path) => path is { Length: > 0 } && !path.Contains("${") && !Path.IsPathRooted(path);

    private static string? Absolute(string? path, string baseDirectory) =>
        IsPlainRelative(path) ? Path.GetFullPath(path!, baseDirectory) : path;

    private static string? Relative(string? path, string fromDirectory, string toDirectory) =>
        IsPlainRelative(path) ? Path.GetRelativePath(toDirectory, Path.GetFullPath(path!, fromDirectory)).Replace('\\', '/') : path;
}
