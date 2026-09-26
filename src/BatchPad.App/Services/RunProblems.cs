using BatchPad.Core.Arguments;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using BatchPad.Core.Trust;

namespace BatchPad.App.Services;

public static class RunProblems
{
    public static bool IsRunProblem(Exception ex) =>
        ex is RunException or UntrustedWorkspaceException or TemplateException or ArgumentAssemblyException;
}
