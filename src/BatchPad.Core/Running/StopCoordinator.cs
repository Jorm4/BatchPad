using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Templating;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Running;

/// <summary>
/// Stops a run as §4 describes: the script's <c>stop</c> companion with the same values, else WM_CLOSE to its windows,
/// then termination after a grace period.
/// </summary>
public sealed class StopCoordinator(RunGate gate, InterpreterLocator interpreters)
{
    public static readonly TimeSpan CompanionGrace = TimeSpan.FromSeconds(10);

    /// <returns>The companion's run, if one was started; the caller shows and disposes it.</returns>
    public async Task<RunHandle?> StopAsync(RunHandle run, RunRequest request)
    {
        var companion = CompanionFor(request);
        RunHandle? companionRun = null;
        await run.StopAsync(RunOutcome.Stopped, companion is null ? RunHandle.DefaultStopGrace : CompanionGrace,
            companion is null ? null : () =>
            {
                try
                {
                    companionRun = gate.Start(companion, interpreters);
                    return true;
                }
                catch (Exception ex) when (ex is RunException or UntrustedWorkspaceException or TemplateException or ArgumentAssemblyException)
                {
                    return false;
                }
            });
        return companionRun;
    }

    /// <summary>The request that runs <paramref name="request"/>'s <c>stop</c> script with the same values; null when it has none.</summary>
    public static RunRequest? CompanionFor(RunRequest request)
    {
        if (request.Script.Stop is not { } reference
            || ReferenceResolver.Parse(reference, request.Tree.Kind) is not { } parsed
            || request.Workspace.References.Resolve(reference, request.Tree.Kind) is not ScriptNode stopScript)
            return null;
        var tree = request.Workspace.Trees.First(t => t.Kind == parsed.Tree);
        return new RunRequest(request.Workspace, tree, stopScript) { Values = request.Values, BaseEnvironment = request.BaseEnvironment };
    }
}
