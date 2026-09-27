using System.ComponentModel;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Cli;

/// <summary><c>batchpad secret</c>: saves the values unattended runs use for <c>secret</c> parameters, read at the keyboard only.</summary>
public sealed class CliSecret(AppPaths paths, TrustStore trust, ISecretStore secrets, IConsolePrompt prompt, TextWriter output,
    TextWriter error, Func<string, string?> environment)
{
    public int Run(CliCommand command, string currentDirectory)
    {
        if (command.Target == "set" && CliTrust.Refusal(command, "save a secret", prompt, environment) is { } refusal)
        {
            error.WriteLine(refusal);
            return CliRunner.Failure;
        }
        LoadedWorkspace? workspace = null;
        if (WorkspaceLocator.Locate(command.Workspace, currentDirectory, []) is { } file && File.Exists(file))
        {
            try
            {
                workspace = WorkspaceLoader.Load(file, paths, trust);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException)
            {
                error.WriteLine(ex.Message);
                return CliRunner.Failure;
            }
        }
        else if (!command.Global)
        {
            error.WriteLine("No batchpad.json found here or above; pass --workspace <path>, or --global for Global scripts.");
            return CliRunner.Failure;
        }
        try
        {
            var scope = command.Global ? SecretScope.Global : SecretScope.Of(workspace!);
            switch (command.Target)
            {
                case "list":
                    foreach (var name in scope.Names(secrets))
                        output.WriteLine(name);
                    return 0;
                case "remove":
                    if (scope.Remove(secrets, command.Parameter!))
                        return 0;
                    error.WriteLine($"No secret '{command.Parameter}' is saved{Where(command, workspace)}.");
                    return CliRunner.Failure;
                default:
                    return Set(command, workspace, scope);
            }
        }
        catch (Win32Exception ex)
        {
            error.WriteLine(ex.Message);
            return CliRunner.Failure;
        }
    }

    private int Set(CliCommand command, LoadedWorkspace? workspace, SecretScope scope)
    {
        var name = command.Parameter!;
        if (workspace is not null && !SecretNames(workspace, command.Global).Contains(name))
            error.WriteLine($"warning: no secret parameter '{name}' is defined{Where(command, workspace)}; saving it anyway.");
        output.Write($"Value for {name}: ");
        if (prompt.ReadSecret() is not { Length: > 0 } value)
        {
            error.WriteLine("Nothing saved.");
            return CliRunner.Failure;
        }
        scope.Set(secrets, name, value);
        output.WriteLine($"Saved {name}{Where(command, workspace)} for scheduled and command-line runs.");
        return 0;
    }

    private static HashSet<string> SecretNames(LoadedWorkspace workspace, bool global)
    {
        var trees = global ? workspace.Global.SelfAndParts() : workspace.Workspace.SelfAndParts().Concat(workspace.MyScripts.SelfAndParts());
        var names = trees.SelectMany(t => t.AllNodes()).Select(n => n.Node).OfType<RunnableNode>()
            .SelectMany(node => SecretFill.SecretParameters(node, workspace)).Select(p => p.StoredAs)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!global)
            names.UnionWith((workspace.Workspace.File.SharedParams ?? []).Where(p => p.Value.Type == ParameterType.Secret).Select(p => p.Key));
        return names;
    }

    private static string Where(CliCommand command, LoadedWorkspace? workspace) =>
        command.Global ? " for Global scripts" : $" for workspace '{workspace!.Id}'";
}
