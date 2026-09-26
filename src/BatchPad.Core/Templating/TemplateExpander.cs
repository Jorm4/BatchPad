using System.Text;

namespace BatchPad.Core.Templating;

public sealed class TemplateException(string message, string variable) : Exception(message)
{
    /// <summary>The variable as written inside <c>${…}</c>, without filters.</summary>
    public string Variable { get; } = variable;
}

/// <summary>
/// Expands <c>${…}</c> variables (§3.4). A template that is exactly one variable keeps its typed value;
/// otherwise the result is text with lists joined by spaces. An unknown variable throws.
/// </summary>
public static class TemplateExpander
{
    public static TemplateValue Expand(string template, TemplateContext context) =>
        new Expansion(context).Expand(template);

    public static string ExpandText(string template, TemplateContext context) => Expand(template, context).Text;

    public static bool HasVariables(string text) => text.Contains("${", StringComparison.Ordinal);

    private sealed class Expansion(TemplateContext context)
    {
        private readonly HashSet<string> variablesBeingExpanded = [];

        public TemplateValue Expand(string template)
        {
            var text = new StringBuilder();
            var position = 0;
            while (true)
            {
                var start = template.IndexOf("${", position, StringComparison.Ordinal);
                if (start < 0)
                    break;
                var end = template.IndexOf('}', start + 2);
                if (end < 0)
                    throw new TemplateException($"Unclosed ${{ in \"{template}\".", template[(start + 2)..]);

                var value = Evaluate(template[(start + 2)..end]);
                if (start == 0 && end == template.Length - 1)
                    return value;
                text.Append(template, position, start - position).Append(value.Text);
                position = end + 1;
            }
            return TemplateValue.Of(text.Append(template, position, template.Length - position).ToString());
        }

        private TemplateValue Evaluate(string expression)
        {
            var parts = expression.Split('|');
            var name = parts[0].Trim();
            var value = Resolve(name);
            foreach (var filter in parts.Skip(1).Select(f => f.Trim()))
            {
                value = filter switch
                {
                    "lower" => value.Map(s => s.ToLowerInvariant()),
                    "upper" => value.Map(s => s.ToUpperInvariant()),
                    "quote" => value.Map(s => $"\"{s}\""),
                    _ => throw new TemplateException($"Unknown filter |{filter} on ${{{name}}}.", name),
                };
            }
            return value;
        }

        private TemplateValue Resolve(string name)
        {
            if (name.StartsWith("param:", StringComparison.Ordinal))
                return ResolveParam(name);
            if (name.StartsWith("env:", StringComparison.Ordinal))
                return context.Environment(name[4..]) is { } envValue ? TemplateValue.Of(envValue) : throw Unknown(name);

            if (context.Params.TryGetValue(name, out var param))
                return param;
            if (context.Variables?.TryGetValue(name, out var variable) == true)
                return ExpandVariable(name, variable);
            if (ResolveBuiltIn(name) is { } builtIn)
                return builtIn;
            if (context.Environment(name) is { } environmentValue)
                return TemplateValue.Of(environmentValue);
            throw Unknown(name);
        }

        private TemplateValue ResolveParam(string name)
        {
            var reference = name["param:".Length..];
            var dot = reference.IndexOf('.');
            var paramName = dot < 0 ? reference : reference[..dot];
            if (!context.Params.TryGetValue(paramName, out var value))
                throw Unknown(name);
            return dot < 0 ? value : Field(value, reference[(dot + 1)..], name);
        }

        private TemplateValue ExpandVariable(string name, string template)
        {
            if (!variablesBeingExpanded.Add(name))
                throw new TemplateException($"Variable ${{{name}}} refers to itself.", name);
            try
            {
                return Expand(template);
            }
            finally
            {
                variablesBeingExpanded.Remove(name);
            }
        }

        private TemplateValue? ResolveBuiltIn(string name)
        {
            switch (name)
            {
                case "workspaceDir": return Text(context.WorkspaceDir);
                case "scriptDir": return Text(context.ScriptDir);
                case "item": return context.Item;
            }

            var segments = name.Split('.');
            return segments[0] switch
            {
                "item" when context.Item is not null && segments.Length == 2 => Field(context.Item, segments[1], name),
                "workflow" when segments.Length == 2 => Text(context.Workflow?.GetValueOrDefault(segments[1])),
                "steps" when segments.Length == 3 && context.Steps?.TryGetValue(segments[1], out var outputs) == true =>
                    Text(outputs.GetValueOrDefault(segments[2])),
                _ => null,
            };
        }

        private static TemplateValue Field(TemplateValue value, string field, string name) =>
            value.Field(field) is { } text ? TemplateValue.Of(text) : throw Unknown(name);

        private static TemplateValue? Text(string? text) => text is null ? null : TemplateValue.Of(text);

        private static TemplateException Unknown(string name) => new($"Unknown variable ${{{name}}}.", name);
    }
}
