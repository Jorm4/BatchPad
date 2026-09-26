using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Trust;

public sealed record GateDecision(bool Allowed, string? Reason)
{
    public static readonly GateDecision Allow = new(true, null);
}

public sealed class UntrustedWorkspaceException(string message) : Exception(message);

/// <summary>The only public way to start a run: nothing runs for an untrusted workspace (§4.3).</summary>
public sealed class RunGate(TrustStore trust, TimeProvider? time = null, LockManager? locks = null)
{
    public TimeProvider Time { get; } = time ?? TimeProvider.System;
    public LockManager Locks { get; } = locks ?? new LockManager();

    public GateDecision Check(LoadedWorkspace workspace) =>
        trust.IsTrusted(workspace.Directory)
            ? GateDecision.Allow
            : new GateDecision(false,
                $"BatchPad runs nothing from '{workspace.Directory}' until you trust this workspace folder, "
                + "because its batchpad.json may have come from someone else.");

    /// <summary>The workspace's trust, and for a file it includes from elsewhere, that folder's trust too.</summary>
    public GateDecision Check(RunRequest request)
    {
        var decision = Check(request.Workspace);
        var folder = request.Tree.BaseDirectory;
        return decision.Allowed && request.Tree.Kind == TreeKind.Workspace && !trust.IsTrusted(folder)
            ? new GateDecision(false, $"BatchPad runs nothing from '{folder}', which this workspace includes, until you trust that folder too.")
            : decision;
    }

    /// <exception cref="UntrustedWorkspaceException">The workspace is not trusted.</exception>
    /// <exception cref="RunException">The first process could not be started.</exception>
    public RunHandle Start(LoadedWorkspace workspace, IReadOnlyList<RunSpec> specs)
    {
        Demand(Check(workspace));
        return ProcessRunner.Start(specs, Time);
    }

    /// <exception cref="UntrustedWorkspaceException">The workspace, or the folder of an included file, is not trusted.</exception>
    /// <exception cref="LockBusyException">A lock is held and <paramref name="waitForLocks"/> is false.</exception>
    public RunHandle Start(RunRequest request, InterpreterLocator interpreters, bool waitForLocks = true)
    {
        Demand(Check(request));
        var specs = RunPlanner.Plan(request, interpreters);
        var locks = LocksFor(request);
        if (locks.Count == 0)
            return ProcessRunner.Start(specs, Time);

        var holder = ScriptTree.DisplayName(request.Script);
        var checkout = request.Workspace.CheckoutDirectory;
        var lease = waitForLocks ? null : Locks.AcquireAsync(locks, wait: false, holder: holder, checkout: checkout).GetAwaiter().GetResult();
        var handle = ProcessRunner.Create(specs, Time);
        if (lease is not null)
        {
            handle.Begin(lease);
            return handle;
        }
        var acquiring = Locks.AcquireAsync(locks, handle.Wait, handle.StopRequested, holder: holder, checkout: checkout);
        if (acquiring.IsCompletedSuccessfully)
            handle.Begin(acquiring.Result);
        else
            _ = BeginWhenLockedAsync(handle, acquiring);
        return handle;
    }

    /// <summary>
    /// The script's <c>lock</c>, held for the request's owner, plus a lock of its own when it is <c>singleInstance</c>, held for
    /// this run alone so two parallel steps of one workflow don't share it and named for the workspace, which its worktrees
    /// share (§4.5). Both are keyed by the script's <c>lockScope</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, object Owner)> LocksFor(RunRequest request)
    {
        var run = new object();
        var script = request.Script;
        var locks = new List<(string, object)>();
        string Key(string name) => LockKeys.For(name, script.LockScope, request.Workspace.CheckoutDirectory);
        if (!string.IsNullOrWhiteSpace(script.Lock))
            locks.Add((Key(script.Lock), request.LockOwner ?? run));
        if (script.SingleInstance == true)
            locks.Add((Key($"single instance of {request.Workspace.Id}/{request.Tree.Kind}:{script.Id ?? script.Path}"), run));
        return locks;
    }

    private static void Demand(GateDecision decision)
    {
        if (!decision.Allowed)
            throw new UntrustedWorkspaceException(decision.Reason!);
    }

    private static async Task BeginWhenLockedAsync(RunHandle handle, Task<LockLease> acquiring)
    {
        LockLease lease;
        try
        {
            lease = await acquiring;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            handle.FailToStart($"Could not take its lock: {ex.Message}");
            return;
        }
        try
        {
            handle.Begin(lease);
        }
        catch (RunException ex)
        {
            handle.FailToStart(ex.Message);
        }
    }
}
