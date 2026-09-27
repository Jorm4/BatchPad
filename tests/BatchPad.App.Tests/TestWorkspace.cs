using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

/// <summary>A private data folder and, optionally, a private copy of <c>samples/demo</c>.</summary>
internal sealed class TestWorkspace : IDisposable
{
    private readonly TempDir _dir = new();

    public static string DemoSource => Path.Combine(AppContext.BaseDirectory, "samples", "demo");

    public string Root => _dir.Root;

    public AppPaths Paths => new(Path.Combine(Root, "data"));

    public string CopyDemo()
    {
        var target = Path.Combine(Root, "demo");
        CopyTree(DemoSource, target);
        return target;
    }

    public MainViewModel OpenMain(string workspaceDirectory, bool trusted = false, Settings? settings = null,
        IRunLauncher? launcher = null, IUiDispatcher? dispatcher = null, IShellService? shell = null, IFileDialogService? dialogs = null,
        IConfirmService? confirm = null, IWorkflowLauncher? workflows = null, TimeProvider? time = null, IAskService? ask = null,
        ITrayService? tray = null, ISecretStore? secrets = null)
    {
        var main = new MainViewModel(Paths, settings ?? new Settings(), launcher, dispatcher, shell, dialogs, confirm, workflows, time, ask, tray,
            secrets ?? new FakeSecretStore());
        if (trusted)
            main.Trust.Trust(workspaceDirectory);
        main.OpenInitial(workspaceDirectory, Root);
        return main;
    }

    public void Dispose() => _dir.Dispose();
}

internal static class MainViewModelExtensions
{
    public static NodeViewModel Select(this MainViewModel main, string automationId)
    {
        var node = main.Tree!.Find(automationId)
            ?? throw new AssertFailedException($"No node {automationId}; have {string.Join(", ", main.Tree.AllNodes.Select(n => n.AutomationId))}");
        node.IsSelected = true;
        return node;
    }
}
