using BatchPad.App.ViewModels.History;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Services;

/// <summary>Starts scheduled runs through the app's launchers; the scheduler records the run, the app records workflow steps.</summary>
public sealed class AppScheduleLauncher(IRunLauncher runs, IWorkflowLauncher workflows, Func<LoadedWorkspace> workspace, HistoryViewModel history)
    : IScheduleLauncher
{
    public IRunOutput Start(RunRequest request) => runs.Start(request);

    public IRunOutput Start(WorkflowRequest request)
    {
        var run = workflows.Start(workspace(), request);
        if (request.Target is null)
            history.RecordSteps(run);
        return new WorkflowRunOutput(run);
    }
}
