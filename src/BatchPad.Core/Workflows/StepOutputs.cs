using System.Text.RegularExpressions;
using BatchPad.Core.Running;

namespace BatchPad.Core.Workflows;

/// <summary>Values a step exports by printing <c>::set name=value</c> lines, read as <c>${steps.&lt;id&gt;.&lt;name&gt;}</c> (§4.1).</summary>
public static partial class StepOutputs
{
    public static bool IsSetLine(string line) => SetLine().IsMatch(line);

    /// <summary>The values set on stdout; a later line for the same name wins.</summary>
    public static IReadOnlyDictionary<string, string> From(IEnumerable<OutputLine> lines)
    {
        var outputs = new Dictionary<string, string>();
        foreach (var line in lines.Where(l => l.Stream == OutputStream.Stdout))
            if (SetLine().Match(line.Text) is { Success: true } match)
                outputs[match.Groups[1].Value] = match.Groups[2].Value.TrimEnd();
        return outputs;
    }

    [GeneratedRegex(@"^::set ([A-Za-z_][\w-]*)=(.*)$")]
    private static partial Regex SetLine();
}
