using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Choices;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Customisation;

/// <summary>Applies My Scripts customisations to their bases (§3.7), so upstream changes to a base show through.</summary>
public sealed class CustomisationResolver(LoadedWorkspace workspace)
{
    private static readonly HashSet<string> NotOverlaid =
    [
        nameof(ScriptNode.Id), nameof(ScriptNode.Base), nameof(ScriptNode.Values), nameof(ScriptNode.ExtraArgs),
        nameof(ScriptNode.StepValues), nameof(ScriptNode.Env), nameof(TreeNode.ExtensionData),
    ];

    private readonly ChoiceResolver choices = new();

    public ResolvedCustomisation Resolve(ScriptNode entry) => Resolve(entry, []);

    private ResolvedCustomisation Resolve(ScriptNode entry, HashSet<ScriptNode> visiting)
    {
        if (entry.Base is not { } reference)
            return WithValues(entry, entry, workspace.MyScripts, entry.Values, entry.StepValues);

        if (!visiting.Add(entry))
            return Broken(entry, $"'{reference}' refers back to itself.");
        var parsed = ReferenceResolver.Parse(reference, TreeKind.MyScripts);
        var baseNode = workspace.References.Resolve(reference, TreeKind.MyScripts);
        if (parsed is null || baseNode is null)
            return Broken(entry, $"The base '{reference}' no longer exists.");
        if (baseNode is not RunnableNode runnable)
            return Broken(entry, $"The base '{reference}' is not a script or workflow.");

        var baseTree = workspace.Trees.First(t => t.Kind == parsed.Value.Tree);
        IReadOnlyDictionary<string, JsonNode?>? baseValues = null;
        IReadOnlyDictionary<string, Dictionary<string, JsonNode?>>? baseStepValues = null;
        if (runnable is ScriptNode { Base: not null } chained)
        {
            var inner = Resolve(chained, visiting);
            if (inner.IsBroken)
                return Broken(entry, inner.Problem!);
            (runnable, baseTree, baseValues, baseStepValues) = (inner.Definition!, inner.DefinitionTree!, inner.Values, inner.StepValues);
        }

        var definition = Overlay(runnable, entry);
        return WithValues(entry, definition, baseTree, Merge(baseValues, entry.Values), MergeSteps(baseStepValues, entry.StepValues));
    }

    /// <summary>The name a customisation gets from its base's <c>nameTemplate</c> and values, else the base's name.</summary>
    public string NameFor(RunnableNode definition, ScriptTree tree, IReadOnlyDictionary<string, JsonNode?>? values)
    {
        var fallback = ScriptTree.DisplayName(definition);
        if (definition.NameTemplate is not { } template)
            return fallback;
        try
        {
            var request = AssemblyFor(definition, tree, values);
            var templates = request.Templates with
            {
                Params = ArgumentAssembler.Bind(request).ToDictionary(p => p.Definition.Name!, p => p.Value),
            };
            return TemplateExpander.ExpandText(template, templates);
        }
        catch (Exception ex) when (ex is TemplateException or ArgumentAssemblyException)
        {
            return fallback;
        }
    }

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ConfigJson.Options), ConfigJson.Options)!;

    private ResolvedCustomisation WithValues(ScriptNode entry, RunnableNode definition, ScriptTree tree,
        IReadOnlyDictionary<string, JsonNode?>? values, IReadOnlyDictionary<string, Dictionary<string, JsonNode?>>? stepValues)
    {
        var problems = new List<string>();
        var normalized = Normalize(definition, tree, values ?? new Dictionary<string, JsonNode?>(), problems);
        return new ResolvedCustomisation(entry)
        {
            Definition = definition,
            DefinitionTree = tree,
            Name = entry.Name ?? NameFor(definition, tree, normalized),
            Values = normalized,
            StepValues = stepValues ?? new Dictionary<string, Dictionary<string, JsonNode?>>(),
            ValueProblems = problems,
        };
    }

    private static ResolvedCustomisation Broken(ScriptNode entry, string problem) => new(entry)
    {
        Name = entry.Name ?? entry.Base ?? "",
        Values = entry.Values ?? new Dictionary<string, JsonNode?>(),
        StepValues = entry.StepValues ?? new Dictionary<string, Dictionary<string, JsonNode?>>(),
        Problem = problem,
    };

    private static RunnableNode Overlay(RunnableNode baseNode, ScriptNode entry)
    {
        var result = Clone<TreeNode>(baseNode) as RunnableNode ?? throw new InvalidOperationException("Clone lost the node type.");
        var overlay = Clone(entry);
        var fields = result is ScriptNode ? typeof(ScriptNode) : typeof(RunnableNode);
        foreach (var property in fields.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanWrite && !NotOverlaid.Contains(property.Name) && property.GetValue(overlay) is { } value)
                property.SetValue(result, value);
        }
        if (overlay.Env is { } env)
        {
            result.Env = new Dictionary<string, string>(result.Env ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in env)
                result.Env[name] = value;
        }
        return result;
    }

    private Dictionary<string, JsonNode?> Normalize(RunnableNode definition, ScriptTree tree,
        IReadOnlyDictionary<string, JsonNode?> values, List<string> problems)
    {
        var normalized = values.ToDictionary(v => v.Key, v => v.Value?.DeepClone());
        var context = ChoiceContextFor(definition, tree);
        foreach (var parameter in SharedParameters.MergeAll(definition.Params, workspace.Workspace.File.SharedParams))
        {
            if (parameter is not { Name: { } name, Type: ParameterType.Choice or ParameterType.Multichoice }
                || !normalized.TryGetValue(name, out var stored) || stored is null)
                continue;
            var known = choices.Resolve(parameter, context).Choices;
            normalized[name] = stored switch
            {
                JsonArray list => new JsonArray([.. list.Select(item => (JsonNode?)Normalized(item, known, name, problems))]),
                JsonValue value => Normalized(value, known, name, problems),
                _ => stored,
            };
        }
        return normalized;
    }

    private static JsonNode? Normalized(JsonNode? stored, IReadOnlyList<ChoiceDefinition> known, string name, List<string> problems)
    {
        if (stored is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            return stored?.DeepClone();
        var result = ValueNormalizer.Normalize(value.GetValue<string>(), known);
        if (result.Error is { } error)
            problems.Add($"{name}: {error}");
        return JsonValue.Create(result.Value);
    }

    private AssemblyRequest AssemblyFor(RunnableNode definition, ScriptTree tree, IReadOnlyDictionary<string, JsonNode?>? values)
    {
        var context = ChoiceContextFor(definition, tree);
        return new AssemblyRequest(definition as ScriptNode ?? new ScriptNode { Params = definition.Params }, context.Templates!)
        {
            SharedParams = workspace.Workspace.File.SharedParams,
            Values = values,
            Choices = p => choices.Resolve(p, context).Choices,
        };
    }

    private ChoiceContext ChoiceContextFor(RunnableNode definition, ScriptTree tree)
    {
        var templates = definition is ScriptNode script
            ? RunPlanner.TemplatesFor(new RunRequest(workspace, tree, script))
            : new TemplateContext { WorkspaceDir = workspace.Directory, Variables = workspace.Workspace.File.Variables };
        return new ChoiceContext(workspace.Directory) { Lists = workspace.Workspace.File.Lists, Templates = templates };
    }

    private static Dictionary<string, JsonNode?>? Merge(IReadOnlyDictionary<string, JsonNode?>? under, IReadOnlyDictionary<string, JsonNode?>? over)
    {
        if (under is null && over is null)
            return null;
        var merged = new Dictionary<string, JsonNode?>(under ?? new Dictionary<string, JsonNode?>());
        foreach (var (name, value) in over ?? new Dictionary<string, JsonNode?>())
            merged[name] = value;
        return merged;
    }

    private static Dictionary<string, Dictionary<string, JsonNode?>>? MergeSteps(
        IReadOnlyDictionary<string, Dictionary<string, JsonNode?>>? under, IReadOnlyDictionary<string, Dictionary<string, JsonNode?>>? over)
    {
        if (under is null && over is null)
            return null;
        var merged = (under ?? new Dictionary<string, Dictionary<string, JsonNode?>>()).ToDictionary(s => s.Key, s => s.Value);
        foreach (var (step, values) in over ?? new Dictionary<string, Dictionary<string, JsonNode?>>())
            merged[step] = Merge(merged.GetValueOrDefault(step), values)!;
        return merged;
    }
}
