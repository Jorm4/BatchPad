using BatchPad.Core.Running;

namespace BatchPad.App.Services;

public sealed record AppServices(
    IRunLauncher Launcher, InterpreterLocator Interpreters, IUiDispatcher Dispatcher, IShellService Shell, IFileDialogService Dialogs,
    IConfirmService Confirm, IWorkflowLauncher Workflows, IAskService Ask)
{
    public IShellOpener Opener { get; } = new ShellOpener(Shell);
}
