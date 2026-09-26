using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.Core.Model;
using BatchPad.Core.Running;

namespace BatchPad.Core.Detection;

/// <summary>Reads a PowerShell script's <c>param()</c> block through the PowerShell parser; the script itself never runs.</summary>
internal static class PowerShellParamDetector
{
    private const string Probe = """
        $path = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{path}'))
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
        $found = @()
        if ($ast.ParamBlock) {
            foreach ($p in $ast.ParamBlock.Parameters) {
                $o = [ordered]@{ name = $p.Name.VariablePath.UserPath; type = $p.StaticType.Name; set = $null; mandatory = $false; help = $null }
                if ($p.DefaultValue) { try { $o.default = $p.DefaultValue.SafeGetValue() } catch { } }
                foreach ($a in $p.Attributes) {
                    if ($a -isnot [System.Management.Automation.Language.AttributeAst]) { continue }
                    if ($a.TypeName.Name -eq 'ValidateSet') { $o.set = @($a.PositionalArguments | ForEach-Object { $_.SafeGetValue() }) }
                    if ($a.TypeName.Name -eq 'Parameter') {
                        foreach ($n in $a.NamedArguments) {
                            if ($n.ArgumentName -eq 'Mandatory') { $o.mandatory = $n.ExpressionOmitted -or [bool]$n.Argument.SafeGetValue() }
                            if ($n.ArgumentName -eq 'HelpMessage') { $o.help = $n.Argument.SafeGetValue() }
                        }
                    }
                }
                $found += [pscustomobject]$o
            }
        }
        ConvertTo-Json -InputObject @($found) -Compress -Depth 4
        """;

    public static List<ParameterDefinition> Detect(string powershell, string path, TimeSpan timeout)
    {
        var script = Probe.Replace("{path}", Convert.ToBase64String(Encoding.Unicode.GetBytes(path)));
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var line = new CommandLine(powershell, ArgvQuoter.Join(["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded]),
            Path.GetDirectoryName(path)!);
        var output = CapturedProcess.Run(line, timeout);
        if (output.ExitCode != 0 || JsonNode.Parse(string.Join('\n', output.Lines)) is not JsonArray found)
            return [];
        return found.OfType<JsonObject>().Select(ToParameter).ToList();
    }

    private static ParameterDefinition ToParameter(JsonObject p)
    {
        var name = p["name"]!.GetValue<string>();
        var type = p["type"]?.GetValue<string>();
        var parameter = new ParameterDefinition
        {
            Name = name,
            Arg = "-" + name,
            Description = p["help"]?.GetValue<string>(),
            Required = p["mandatory"]?.GetValueKind() == JsonValueKind.True ? true : null,
        };
        if (type == "SwitchParameter")
        {
            parameter.Type = ParameterType.Flag;
            return parameter;
        }
        if (p["set"] is JsonArray set)
        {
            parameter.Type = type?.EndsWith("[]") == true ? ParameterType.Multichoice : ParameterType.Choice;
            parameter.Choices = set.Select(v => new ChoiceDefinition { Value = ScriptProbes.Text(v) }).ToList();
        }
        else if (type is "Int32" or "Int64" or "Int16" or "Byte" or "UInt32")
            parameter.Type = ParameterType.Int;
        else
        {
            parameter.Type = ParameterType.Text;
            parameter.Split = type?.EndsWith("[]") == true ? true : null;
        }

        if (p["default"] is JsonValue value)
            parameter.Default = ScriptProbes.DefaultFor(parameter, value);
        return parameter;
    }

}
