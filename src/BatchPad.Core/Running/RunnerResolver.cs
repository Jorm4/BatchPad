using System.Diagnostics;
using BatchPad.Core.Model;
using BatchPad.Core.Templating;

namespace BatchPad.Core.Running;

/// <summary>An exact process command line; <see cref="Arguments"/> is passed to CreateProcess verbatim.</summary>
public sealed record CommandLine(string FileName, string Arguments, string WorkingDirectory)
{
    public string Display => Join(FileName, Arguments);

    /// <summary>For reading, not running: paths under <paramref name="directory"/> become relative and other programs show by name.</summary>
    public string DisplayRelativeTo(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var program = FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? FileName[root.Length..]
            : Path.IsPathFullyQualified(FileName) ? Path.GetFileNameWithoutExtension(FileName)
            : FileName;
        return Join(program, Arguments.Replace(root, "", StringComparison.OrdinalIgnoreCase));
    }

    private static string Join(string program, string arguments) =>
        arguments.Length == 0 ? ArgvQuoter.Quote(program) : $"{ArgvQuoter.Quote(program)} {arguments}";

    public ProcessStartInfo ToStartInfo() => new(FileName, Arguments) { WorkingDirectory = WorkingDirectory, UseShellExecute = false };
}

/// <summary>Picks a script's runner and builds its command line from the §4 table.</summary>
public sealed class RunnerResolver(InterpreterLocator interpreters)
{
    public static Runner EffectiveRunner(ScriptNode script)
    {
        if (script.Runner is { } runner and not Runner.Auto)
            return runner;
        if (script.Command is not null)
            return Runner.Shell;
        if (script.Module is not null)
            return Runner.Python;
        return Path.GetExtension(script.Path)?.ToLowerInvariant() switch
        {
            ".bat" or ".cmd" => Runner.Batch,
            ".py" or ".pyw" => Runner.Python,
            ".cs" => Runner.Csharp,
            ".ps1" => Runner.Powershell,
            ".exe" or ".com" => Runner.Exe,
            _ => throw new RunException($"Cannot tell how to run '{script.Path}'; set its runner."),
        };
    }

    /// <param name="baseDirectory">The folder of the config file that defines the script; relative paths start here.</param>
    public CommandLine Resolve(ScriptNode script, IReadOnlyList<string> arguments, string baseDirectory,
        TemplateContext? templates = null, bool keepWindowOpen = false)
    {
        var runner = EffectiveRunner(script);
        string Expand(string text) => templates is null ? text : TemplateExpander.ExpandText(text, templates);

        var scriptPath = script.Path is null ? null : Path.GetFullPath(Path.Combine(baseDirectory, Expand(script.Path)));
        if (scriptPath is not null && !File.Exists(scriptPath))
            throw new RunException(runner == Runner.Exe ? $"'{scriptPath}' is not built yet." : $"'{scriptPath}' was not found.");

        var workingDirectory = WorkingDirectoryFor(script, runner, scriptPath, baseDirectory, templates);

        var command = runner switch
        {
            Runner.Batch => ThroughCmd(CmdEscaper.Escape($"\"{Required(scriptPath, runner)}\""), arguments, keepWindowOpen, workingDirectory),
            Runner.Shell => ThroughCmd(script.Command ?? throw new RunException("A shell entry needs a command."), arguments, keepWindowOpen, workingDirectory),
            Runner.Python => Python(script, scriptPath, arguments, workingDirectory),
            Runner.Csharp => Direct(interpreters.Dotnet() ?? throw new RunException("dotnet was not found."),
                ["run", "--file", Required(scriptPath, runner), "--", .. arguments], workingDirectory),
            Runner.Powershell => Direct(interpreters.PowerShell(),
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Required(scriptPath, runner), .. arguments], workingDirectory),
            Runner.Exe => Direct(Required(scriptPath, runner), arguments, workingDirectory),
            _ => throw new RunException($"Runner '{runner}' is not supported."),
        };
        return keepWindowOpen && runner is not (Runner.Batch or Runner.Shell) ? KeepOpen(command) : command;
    }

    public static string WorkingDirectoryFor(ScriptNode script, Runner runner, string? scriptPath, string baseDirectory, TemplateContext? templates) =>
        script.WorkingDir is not null
            ? Path.GetFullPath(Path.Combine(baseDirectory, templates is null ? script.WorkingDir : TemplateExpander.ExpandText(script.WorkingDir, templates)))
            : runner == Runner.Exe ? Path.GetDirectoryName(scriptPath)! : templates?.WorkspaceDir ?? baseDirectory;

    private CommandLine Python(ScriptNode script, string? scriptPath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var python = interpreters.Python() ?? throw new RunException("Python was not found (neither py nor python).");
        List<string> target = script.Module is not null ? ["-m", script.Module] : [Required(scriptPath, Runner.Python)];
        return Direct(python.Path, [.. python.LeadingArguments, "-u", .. target, .. arguments], workingDirectory);
    }

    private static CommandLine Direct(string program, IEnumerable<string> arguments, string workingDirectory) =>
        new(program, ArgvQuoter.Join(arguments), workingDirectory);

    private CommandLine ThroughCmd(string command, IReadOnlyList<string> arguments, bool keepWindowOpen, string workingDirectory)
    {
        var line = string.Join(' ', [command, .. arguments.Select(CmdEscaper.EscapeBatchArgument)]);
        return new(interpreters.Cmd, $"/d /v:off /s /{(keepWindowOpen ? 'k' : 'c')} \"{line}\"", workingDirectory);
    }

    private CommandLine KeepOpen(CommandLine command) =>
        new(interpreters.Cmd, $"/d /v:off /s /k \"{CmdEscaper.Escape(command.Display)}\"", command.WorkingDirectory);

    private static string Required(string? scriptPath, Runner runner) =>
        scriptPath ?? throw new RunException($"A {runner.ToString().ToLowerInvariant()} entry needs a path.");
}
