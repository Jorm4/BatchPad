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

    /// <exception cref="UntrustedWorkspaceException">The workspace is not trusted.</exception>
    /// <exception cref="RunException">The first process could not be started.</exception>
    public RunHandle Start(LoadedWorkspace workspace, IReadOnlyList<RunSpec> specs)
    {
        if (Check(workspace) is { Allowed: false, Reason: var reason })
            throw new UntrustedWorkspaceException(reason!);
        return ProcessRunner.Start(specs, Time);
    }

    public RunHandle Start(RunRequest request, InterpreterLocator interpreters)
    {
        if (Check(request.Workspace) is { Allowed: false, Reason: var reason })
            throw new UntrustedWorkspaceException(reason!);
        var specs = RunPlanner.Plan(request, interpreters);
        var locks = LocksFor(request);
        if (locks.Count == 0)
            return ProcessRunner.Start(specs, Time);

        var handle = ProcessRunner.Create(specs, Time);
        var acquiring = Locks.AcquireAsync(locks, handle.Wait, handle.StopRequested);
        if (acquiring.IsCompletedSuccessfully)
            handle.Begin(acquiring.Result);
        else
            _ = BeginWhenLockedAsync(handle, acquiring);
        return handle;
    }

    /// <summary>
    /// The script's <c>lock</c>, held for the request's owner, plus a lock of its own when it is <c>singleInstance</c>, held for
    /// this run alone so two parallel steps of one workflow don't share it.
    /// </summary>
    public static IReadOnlyList<(string Name, object Owner)> LocksFor(RunRequest request)
    {
        var run = new object();
        var locks = new List<(string, object)>();
        if (!string.IsNullOrWhiteSpace(request.Script.Lock))
            locks.Add((request.Script.Lock, request.LockOwner ?? run));
        if (request.Script.SingleInstance == true)
            locks.Add(($"single instance of {request.Tree.Kind}:{request.Script.Id ?? request.Script.Path} in {request.Workspace.Directory}", run));
        return locks;
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
