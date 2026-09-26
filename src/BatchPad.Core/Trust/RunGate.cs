using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Trust;

public sealed record GateDecision(bool Allowed, string? Reason)
{
    public static readonly GateDecision Allow = new(true, null);
}

public sealed class UntrustedWorkspaceException(string message) : Exception(message);

/// <summary>The only public way to start a run: nothing runs for an untrusted workspace (§4.3).</summary>
public sealed class RunGate(TrustStore trust)
{
    public GateDecision Check(LoadedWorkspace workspace) =>
        trust.IsTrusted(workspace.Directory)
            ? GateDecision.Allow
            : new GateDecision(false,
                $"BatchPad runs nothing from '{workspace.Directory}' until you trust this workspace folder, "
                + "because its batchpad.json may have come from someone else.");

    /// <exception cref="UntrustedWorkspaceException">The workspace is not trusted.</exception>
    /// <exception cref="RunException">The first process could not be started.</exception>
    public RunHandle Start(LoadedWorkspace workspace, IReadOnlyList<RunSpec> specs)
    {
        if (Check(workspace) is { Allowed: false, Reason: var reason })
            throw new UntrustedWorkspaceException(reason!);
        return ProcessRunner.Start(specs);
    }

    public RunHandle Start(RunRequest request, InterpreterLocator interpreters)
    {
        if (Check(request.Workspace) is { Allowed: false, Reason: var reason })
            throw new UntrustedWorkspaceException(reason!);
        return ProcessRunner.Start(RunPlanner.Plan(request, interpreters));
    }
}
