using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BatchPad.Core.Choices;
using BatchPad.Core.Model;
using BatchPad.Core.Templating;

namespace BatchPad.Core.Arguments;

/// <param name="Templates">Directories, file variables and environment; parameter values are added by the assembler.</param>
public sealed record AssemblyRequest(ScriptNode Script, TemplateContext Templates)
{
    public IReadOnlyDictionary<string, ParameterDefinition>? SharedParams { get; init; }

    /// <summary>Current values by parameter name; a missing name falls back to the parameter's <c>default</c>.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Values { get; init; }

    /// <summary>Workspace-level <c>env</c>, applied before the script's own.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Resolved choices (e.g. from <c>ChoiceResolver</c>) for looking up rich choices; defaults to the fixed <c>choices</c>.</summary>
    public Func<ParameterDefinition, IReadOnlyList<ChoiceDefinition>>? Choices { get; init; }
}

public sealed record BoundParameter(ParameterDefinition Definition, TemplateValue Value)
{
    public bool IsOn => Definition.Type == ParameterType.Flag && Value.Text == "true";
}

/// <summary>Builds the arguments and environment of a script run (§3.3 argument assembly).</summary>
public static partial class ArgumentAssembler
{
    public static IReadOnlyList<Invocation> Assemble(AssemblyRequest request)
    {
        var parameters = Bind(request);
        var templates = request.Templates with { Params = parameters.ToDictionary(p => p.Definition.Name!, p => p.Value) };

        var batched = parameters.FirstOrDefault(p =>
            p.Definition is { Type: ParameterType.Multichoice, MaxPerCall: > 0 } && p.Definition.Emit != false
            && p.Definition.EnvVar is null && p.Value.AsList().Count > p.Definition.MaxPerCall);
        if (batched is null)
            return [Build(request, templates, parameters, null, null)];

        return batched.Value.AsList()
            .Chunk(batched.Definition.MaxPerCall!.Value)
            .Select(chunk => Build(request, templates, parameters, batched, chunk))
            .ToList();
    }

    /// <summary>Each parameter with its current value, <c>use</c> merged; the values <c>${param:…}</c> sees.</summary>
    public static IReadOnlyList<BoundParameter> Bind(AssemblyRequest request)
    {
        var bound = new List<BoundParameter>();
        foreach (var definition in SharedParameters.MergeAll(request.Script.Params, request.SharedParams))
        {
            if (definition.Name is null)
                throw new ArgumentAssemblyException("A parameter has neither a name nor a use.");
            JsonNode? given = null;
            var explicitValue = request.Values?.TryGetValue(definition.Name, out given) == true;
            var raw = explicitValue ? given : definition.Default;
            var value = ToValue(definition, raw, expandText: !explicitValue, request);
            bound.Add(new BoundParameter(definition, value));
        }
        return bound;
    }

    private static TemplateValue ToValue(ParameterDefinition definition, JsonNode? raw, bool expandText, AssemblyRequest request)
    {
        var value = raw switch
        {
            null => TemplateValue.Empty,
            JsonArray array => TemplateValue.OfList(array.Select(ScalarText)),
            _ => TemplateValue.Of(ScalarText(raw)),
        };
        if (expandText && !value.IsList && TemplateExpander.HasVariables(value.Text))
            value = TemplateExpander.Expand(value.Text, request.Templates);

        switch (definition.Type)
        {
            case ParameterType.Flag:
                return TemplateValue.Of(value.Text.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false");
            case ParameterType.Multichoice:
                var items = value.IsList ? value.Items! : SplitArguments(value.Text);
                var options = ChoicesOf(definition, request);
                return TemplateValue.OfList(items.Select(item => Normalize(item, options)));
            case ParameterType.Choice:
                var choices = ChoicesOf(definition, request);
                var normalized = Normalize(value.Text, choices);
                return choices.FirstOrDefault(c => c.Value == normalized) is { } choice ? TemplateValue.OfChoice(choice) : value;
            default:
                return value;
        }
    }

    private static IReadOnlyList<ChoiceDefinition> ChoicesOf(ParameterDefinition definition, AssemblyRequest request) =>
        request.Choices?.Invoke(definition) ?? definition.Choices ?? [];

    private static string Normalize(string value, IReadOnlyList<ChoiceDefinition> choices)
    {
        var normalized = ValueNormalizer.Normalize(value, choices);
        return normalized.Error is { } error ? throw new ArgumentAssemblyException(error) : normalized.Value;
    }

    private static string ScalarText(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => node.ToJsonString(),
    };

    private static Invocation Build(AssemblyRequest request, TemplateContext templates, IReadOnlyList<BoundParameter> parameters,
        BoundParameter? batched, IReadOnlyList<string>? batch)
    {
        var script = request.Script;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in (request.Environment ?? new Dictionary<string, string>()).Concat(script.Env ?? []))
            environment[name] = TemplateExpander.ExpandText(value, templates);

        var arguments = new List<string>();
        foreach (var argument in script.Args ?? [])
            arguments.AddRange(TemplateExpander.Expand(argument, templates).AsList());

        var trailing = new List<string>();
        var parameterEnvironment = new List<string>();
        foreach (var parameter in parameters)
        {
            var definition = parameter.Definition;
            if (definition.Emit == false)
                continue;
            if (definition.EnvVar is { } variable)
            {
                var text = definition.Type == ParameterType.Flag ? (parameter.IsOn ? "1" : "") : parameter.Value.Text;
                if (text.Length > 0)
                {
                    environment[variable] = text;
                    parameterEnvironment.Add(variable);
                }
                continue;
            }
            if (script.ArgsTemplate is not null)
                continue;

            var target = definition.Position == "end" ? trailing : arguments;
            Emit(definition, parameter, ItemsOf(parameter, batched, batch), target);
        }
        arguments.AddRange(trailing);
        if (script.ArgsTemplate is { } template)
            arguments.AddRange(ExpandArgsTemplate(template, templates, parameters, batched, batch));

        var display = Display(script, templates, arguments, parameterEnvironment.Select(n => $"{n}={environment[n]}"));
        return new Invocation(arguments, environment, display);
    }

    private static IReadOnlyList<string> ItemsOf(BoundParameter parameter, BoundParameter? batched, IReadOnlyList<string>? batch) =>
        ReferenceEquals(parameter, batched) ? batch! : parameter.Value.AsList();

    [GeneratedRegex(@"(?<!\$)\{([^{}.]+)(\.\.\.)?\}")]
    private static partial Regex Placeholder();

    private static IEnumerable<string> ExpandArgsTemplate(IReadOnlyList<string> template, TemplateContext templates,
        IReadOnlyList<BoundParameter> parameters, BoundParameter? batched, IReadOnlyList<string>? batch)
    {
        BoundParameter Find(string name) => parameters.FirstOrDefault(p => p.Definition.Name == name)
            ?? throw new ArgumentAssemblyException($"argsTemplate names an unknown parameter '{name}'.");

        foreach (var element in template)
        {
            var text = TemplateExpander.ExpandText(element, templates);
            var spread = Placeholder().Matches(text).FirstOrDefault(m => m.Groups[2].Success);
            if (spread is not null)
            {
                var prefix = Substitute(text[..spread.Index]);
                var suffix = Substitute(text[(spread.Index + spread.Length)..]);
                foreach (var item in ItemsOf(Find(spread.Groups[1].Value), batched, batch))
                    yield return prefix + item + suffix;
                continue;
            }
            var expanded = Substitute(text);
            if (expanded.Length > 0)
                yield return expanded;
        }

        string Substitute(string text) => Placeholder().Replace(text, m =>
        {
            var parameter = Find(m.Groups[1].Value);
            if (parameter.Definition.Type == ParameterType.Flag)
                return parameter.IsOn ? parameter.Definition.Arg ?? "true" : "";
            return string.Join(' ', ItemsOf(parameter, batched, batch));
        });
    }

    private static void Emit(ParameterDefinition definition, BoundParameter parameter, IReadOnlyList<string> items, List<string> target)
    {
        switch (definition.Type)
        {
            case ParameterType.Flag:
                if (parameter.IsOn && definition.Arg is not null)
                    target.Add(definition.Arg);
                return;
            case ParameterType.Multichoice:
                if (items.Count == 0)
                    target.AddRange(definition.EmptyArgs ?? []);
                else
                    EmitValues(definition.Arg, items, definition.RepeatArg == true, target);
                return;
            default:
                var split = definition.Split == true || parameter.Value.Choice?.Split == true;
                var text = parameter.Value.Text;
                if (text.Length > 0)
                    EmitValues(definition.Arg, split ? SplitArguments(text) : [text], repeatArg: false, target);
                return;
        }
    }

    private static void EmitValues(string? arg, IReadOnlyList<string> values, bool repeatArg, List<string> target)
    {
        if (arg is null)
            target.AddRange(values);
        else if (arg.EndsWith('='))
            target.AddRange(values.Select(v => arg + v));
        else if (repeatArg)
            target.AddRange(values.SelectMany(v => new[] { arg, v }));
        else
            target.AddRange([arg, .. values]);
    }

    /// <summary>Splits on whitespace; double quotes group and are removed.</summary>
    public static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                    result.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken)
            result.Add(current.ToString());
        return result;
    }

    private static string Display(ScriptNode script, TemplateContext templates, IEnumerable<string> arguments, IEnumerable<string> environment)
    {
        var program = script.Path is not null ? TemplateExpander.ExpandText(script.Path, templates)
            : script.Command ?? (script.Module is not null ? $"-m {script.Module}" : "");
        return string.Join(' ', environment.Append(program).Concat(arguments.Select(DisplayQuote)).Where(s => s.Length > 0));
    }

    private static string DisplayQuote(string argument) =>
        argument.Length == 0 ? "\"\"" :
        argument.Any(c => char.IsWhiteSpace(c) || c == '"') ? $"\"{argument.Replace("\"", "\\\"")}\"" :
        argument;
}
