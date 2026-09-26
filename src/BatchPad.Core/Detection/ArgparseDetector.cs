using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.Core.Model;
using BatchPad.Core.Running;

namespace BatchPad.Core.Detection;

/// <summary>Reads a Python script's <c>argparse</c> calls through <c>ast</c> in a helper process; the script itself never runs.</summary>
internal static class ArgparseDetector
{
    private static readonly Lazy<string> Probe = new(() =>
    {
        using var stream = typeof(ArgparseDetector).Assembly.GetManifestResourceStream("BatchPad.Core.Detection.argparse_probe.py")!;
        return new StreamReader(stream).ReadToEnd();
    });

    public static List<ParameterDefinition> Detect(Interpreter python, string path, TimeSpan timeout)
    {
        var line = new CommandLine(python.Path, ArgvQuoter.Join([.. python.LeadingArguments, "-I", "-", path]), Path.GetDirectoryName(path)!);
        var output = CapturedProcess.Run(line, timeout, Probe.Value);
        if (output.ExitCode != 0 || JsonNode.Parse(string.Join('\n', output.Lines)) is not JsonArray calls)
            return [];
        return calls.OfType<JsonObject>().Select(ToParameter).OfType<ParameterDefinition>().ToList();
    }

    private static ParameterDefinition? ToParameter(JsonObject call)
    {
        var names = call["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var action = StringOrNull(call["action"]);
        if (names.Contains("-h") || names.Contains("--help") || action is "help" or "version")
            return null;

        var isOption = names[0].StartsWith('-');
        var flag = names.FirstOrDefault(n => n.StartsWith("--")) ?? names[0];
        var nargs = call["nargs"] is { } n ? ScriptProbes.Text(n) : null;
        var isList = nargs is "*" or "+" || int.TryParse(nargs, out var count) && count > 1;
        var parameter = new ParameterDefinition
        {
            Name = StringOrNull(call["dest"]) ?? flag.TrimStart('-').Replace('-', '_'),
            Arg = isOption ? flag : null,
            Description = StringOrNull(call["help"]),
        };

        if (action is "store_true" or "store_false" or "store_const" or "count")
        {
            parameter.Type = ParameterType.Flag;
            return parameter;
        }

        if (call["choices"] is JsonArray choices)
        {
            parameter.Type = isList ? ParameterType.Multichoice : ParameterType.Choice;
            parameter.Choices = choices.Select(c => new ChoiceDefinition { Value = ScriptProbes.Text(c) }).ToList();
        }
        else if (isList)
        {
            parameter.Type = ParameterType.Text;
            parameter.Split = true;
            if (!isOption)
                parameter.Position = "end";
        }
        else
            parameter.Type = StringOrNull(call["type"]) == "int" ? ParameterType.Int : ParameterType.Text;

        if (call["default"] is { } value && value.GetValueKind() is not (JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Null))
            parameter.Default = ScriptProbes.DefaultFor(parameter, value);
        if (call["required"]?.GetValueKind() == JsonValueKind.True || !isOption && nargs is null or "+")
            parameter.Required = true;
        return parameter;
    }

    private static string? StringOrNull(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

}
