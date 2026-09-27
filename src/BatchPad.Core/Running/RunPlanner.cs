using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BatchPad.Core.Arguments;
using BatchPad.Core.Choices;
using BatchPad.Core.Model;
using BatchPad.Core.Templating;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Running;

/// <summary>A script to run from a loaded workspace, with the values the user chose.</summary>
public sealed record RunRequest(LoadedWorkspace Workspace, ScriptTree Tree, ScriptNode Script)
{
    public IReadOnlyDictionary<string, JsonNode?>? Values { get; init; }
    public IEnumerable<KeyValuePair<string, string>>? BaseEnvironment { get; init; }
    public ConsoleMode? Console { get; init; }
    public string? ExtraArguments { get; init; }

    /// <summary>Who takes the run's locks; a workflow passes its own so its steps can re-enter its locks.</summary>
    public object? LockOwner { get; init; }

    /// <summary>Nobody can answer prompts (a schedule, the CLI): a missing <c>ask</c> or <c>secret</c> value or confirmation fails the run.</summary>
    public bool Unattended { get; init; }

    public bool Confirmed { get; init; }

    /// <summary>The script's parameters with <c>use</c> resolved against the workspace's shared ones.</summary>
    public IReadOnlyList<ParameterDefinition> Parameters => SharedParameters.MergeAll(Script.Params, Workspace.Workspace.File.SharedParams);
}

/// <summary>Turns a <see cref="RunRequest"/> into the <see cref="RunSpec"/>s to start, one per invocation.</summary>
public static partial class RunPlanner
{
    private static readonly Dictionary<string, string> CapturedDefaults = new()
    {
        ["PYTHONIOENCODING"] = "utf-8",
        ["FORCE_COLOR"] = "1",
    };

    /// <exception cref="RunException">An unattended run lacks a value or confirmation it would have to ask for.</exception>
    public static IReadOnlyList<RunSpec> Plan(RunRequest request, InterpreterLocator interpreters)
    {
        if (request.Unattended)
            CheckUnattended(request);
        var workspace = request.Workspace;
        var workspaceFile = workspace.Workspace.File;
        var script = request.Script;
        var baseDirectory = request.Tree.BaseDirectory;
        var templates = TemplatesFor(request);
        var assembly = AssemblyFor(request, templates);
        var parameters = ArgumentAssembler.Bind(assembly);
        var invocations = ArgumentAssembler.Assemble(assembly, parameters);
        var withParams = ArgumentAssembler.WithParams(templates, parameters);
        var console = request.Console ?? script.Console ?? ConsoleMode.Captured;
        var timeout = script.LongRunning == true ? null : ParseDuration(script.Timeout);
        var resolver = new RunnerResolver(interpreters);
        var extra = string.IsNullOrWhiteSpace(request.ExtraArguments) ? [] : ArgumentAssembler.SplitArguments(request.ExtraArguments);

        var baseEnvironment = (request.BaseEnvironment is { } given ? new EnvironmentBuilder(given) : EnvironmentBuilder.FromCurrentProcess())
            .Apply(console == ConsoleMode.Captured ? CapturedDefaults : null)
            .ApplyFile(RelativeTo(workspace.Directory, workspaceFile.EnvFile, templates))
            .Apply(workspaceFile.Env?.ToDictionary(e => e.Key, e => TemplateExpander.ExpandText(e.Value, templates)))
            .ApplyFile(RelativeTo(baseDirectory, script.EnvFile, withParams))
            .Build();

        return invocations.Select(invocation =>
        {
            var environment = new EnvironmentBuilder(baseEnvironment).Apply(invocation.Environment).Build();
            var command = resolver.Resolve(script, [.. invocation.Arguments, .. extra], baseDirectory, withParams,
                keepWindowOpen: console == ConsoleMode.WindowKeepOpen);
            return new RunSpec(command, environment) { Console = console, Timeout = timeout };
        }).ToList();
    }

    public const string NeedsAValue = "needs a value for";

    /// <exception cref="RunException">The run needs confirmation or a value nobody supplied (§4 "Unattended runs").</exception>
    public static void CheckUnattended(RunRequest request)
    {
        var name = ScriptTree.DisplayName(request.Script);
        if (request.Script.Confirm is { Length: > 0 } && !request.Confirmed)
            throw new RunException($"'{name}' needs confirmation, and nobody is there to give it.");
        foreach (var parameter in Prompted(request.Script, request.Workspace))
        {
            if (request.Values?.GetValueOrDefault(parameter.Name!) is null)
                throw new RunException($"'{name}' {NeedsAValue} {parameter.Label ?? parameter.Name}.");
        }
    }

    /// <summary>The parameters a run asks for when it starts: <c>ask</c> and <c>secret</c> ones.</summary>
    public static IReadOnlyList<ParameterDefinition> Prompted(RunnableNode node, LoadedWorkspace workspace) =>
        [.. SharedParameters.MergeAll(node.Params, workspace.Workspace.File.SharedParams)
            .Where(p => p.Name is not null && (p.Ask == true || p.Type == ParameterType.Secret))];

    /// <summary>The folder a run starts in, where relative paths in its output point; the workspace folder when it can't be worked out.</summary>
    public static string WorkingDirectoryFor(RunRequest request)
    {
        try
        {
            var templates = BoundTemplatesFor(request);
            var script = request.Script;
            var scriptPath = script.Path is null ? null : TemplateExpander.ExpandPath(script.Path, request.Tree.BaseDirectory, templates);
            return RunnerResolver.WorkingDirectoryFor(script, RunnerResolver.EffectiveRunner(script), scriptPath, request.Tree.BaseDirectory, templates);
        }
        catch (Exception ex) when (ex is TemplateException or RunException or ArgumentAssemblyException or IOException or ArgumentException)
        {
            return request.Workspace.Directory;
        }
    }

    /// <summary>The variables a script's defaults, paths and choice sources expand with, before parameters are bound.</summary>
    public static TemplateContext TemplatesFor(RunRequest request) => new()
    {
        WorkspaceDir = request.Workspace.Directory,
        ScriptDir = request.Script.Path is { } path && !TemplateExpander.HasVariables(path)
            ? Path.GetDirectoryName(TemplateExpander.ExpandPath(path, request.Tree.BaseDirectory, null))
            : null,
        Variables = request.Workspace.Workspace.File.Variables,
    };

    /// <summary>The variables with the request's parameter values bound, as paths, <c>ready.open</c> and artifacts see them.</summary>
    public static TemplateContext BoundTemplatesFor(RunRequest request)
    {
        var templates = TemplatesFor(request);
        return ArgumentAssembler.WithParams(templates, ArgumentAssembler.Bind(AssemblyFor(request, templates)));
    }

    private static AssemblyRequest AssemblyFor(RunRequest request, TemplateContext templates)
    {
        var choices = new ChoiceResolver();
        var choiceContext = ChoicesFor(request, templates);
        return new AssemblyRequest(request.Script, templates)
        {
            SharedParams = request.Workspace.Workspace.File.SharedParams,
            Values = request.Values,
            Choices = p => choices.Resolve(p, choiceContext).Choices,
        };
    }

    public static ChoiceContext ChoicesFor(RunRequest request, TemplateContext? templates = null) =>
        new(request.Workspace.Directory)
        {
            Lists = request.Workspace.Workspace.File.Lists,
            Templates = templates ?? TemplatesFor(request),
            Paths = request.Tree.Paths,
        };

    /// <summary>Parses <c>90s</c>, <c>30m</c>, <c>1h30m</c> or <c>500ms</c>; null when unset.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var compact = text.Replace(" ", "");
        var parts = DurationPart().Matches(compact);
        if (parts.Sum(m => m.Length) != compact.Length || parts.Count == 0)
            throw new RunException($"'{text}' is not a duration such as 30s, 5m or 1h30m.");

        return parts.Aggregate(TimeSpan.Zero, (total, part) =>
        {
            var amount = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            return total + part.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                _ => TimeSpan.FromHours(amount),
            };
        });
    }

    private static string? RelativeTo(string directory, string? path, TemplateContext templates) =>
        path is null ? null : TemplateExpander.ExpandPath(path, directory, templates);

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ms|s|m|h)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPart();
}
