using System.ComponentModel;
using BatchPad.App.Services;
using BatchPad.App.ViewModels.Editor;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using BatchPad.Core.Trust;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public sealed partial class DetailsViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunnable), nameof(IsWorkflow), nameof(IsLink), nameof(IsMyScript), nameof(CanSaveAsMyScript),
        nameof(CustomisesText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(RunInWindowCommand), nameof(StopCommand), nameof(CopyPreviewCommand),
        nameof(EditCommand), nameof(OpenLinkCommand), nameof(SaveCommand), nameof(SaveAsMyScriptCommand), nameof(DuplicateCommand),
        nameof(OpenChangeBaseCommand))]
    private NodeViewModel? node;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenChangeBaseCommand))]
    private ChangeBaseViewModel? changeBase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    private ScriptEditorViewModel? editor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    private WorkflowEditorViewModel? workflowEditor;

    public bool IsEditing => Editor is not null || WorkflowEditor is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyPreviewCommand))]
    private string preview = "";

    [ObservableProperty]
    private string? runError;

    [ObservableProperty]
    private ParameterFormViewModel? form;

    public string? ValidationMessage => Form?.ErrorSummary;

    /// <summary>What runs: a customisation's resolved definition, else the node's own.</summary>
    private (RunnableNode Definition, ScriptTree Tree)? Target =>
        Node?.Customisation is { } customisation
            ? customisation is { IsBroken: false, Definition: { } definition, DefinitionTree: { } tree } ? (definition, tree) : null
            : Node is { IsRunnable: true, Node: RunnableNode runnable } ? (runnable, Node.Tree) : null;

    public bool IsRunnable => Target is not null;

    public bool IsWorkflow => Target?.Definition is WorkflowNode;

    public bool IsMyScript => Node is { IsMyScript: true, Kind: not NodeKind.Folder };

    public bool CanSaveAsMyScript => MyScriptsViewModel.CanAdd(Node);

    public string? CustomisesText
    {
        get
        {
            if (Node?.Customisation is not { Entry.Base: { } reference } || main.Workspace is not { } workspace)
                return null;
            var baseNode = workspace.References.Resolve(reference, TreeKind.MyScripts);
            return main.Tree?.AllNodes.FirstOrDefault(n => n.Node is not null && ReferenceEquals(n.Node, baseNode)) is { } found
                ? $"Customises: {found.Location} › {found.Name}"
                : $"Customises: {reference}";
        }
    }

    public bool IsLink => Node?.Node is LinkNode;

    partial void OnNodeChanged(NodeViewModel? oldValue, NodeViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnNodePropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnNodePropertyChanged;
        RunError = null;
        Editor = null;
        WorkflowEditor = null;
        ChangeBase = null;
        Form = null;
        Form = BuildForm();
        Refresh();
    }

    public void Refresh()
    {
        Preview = BuildPreview();
        OnPropertyChanged(nameof(ValidationMessage));
        RunCommand.NotifyCanExecuteChanged();
        RunInWindowCommand.NotifyCanExecuteChanged();
    }

    public RunRequest? BuildRequest(ConsoleMode? console = null)
    {
        if (main.Workspace is not { } workspace || Target is not { Definition: ScriptNode script, Tree: var tree })
            return null;
        var request = Node!.Customisation is { } customisation
            ? customisation.ToRunRequest(workspace)
            : new RunRequest(workspace, tree, script);
        return request with
        {
            Console = console,
            Values = Form?.Values ?? request.Values,
            ExtraArguments = Form?.ExtraArguments ?? request.ExtraArguments,
        };
    }

    public WorkflowRequest? BuildWorkflowRequest()
    {
        if (Target is not { Definition: WorkflowNode workflow, Tree: var tree })
            return null;
        var request = Node!.Customisation is { } customisation ? WorkflowRequest.From(customisation) : new WorkflowRequest(tree, workflow);
        return request with { Values = Form?.Values ?? request.Values };
    }

    private bool CanRun() => IsRunnable && main.IsTrusted && Form?.HasErrors != true;

    private bool CanRunInWindow() => CanRun() && !IsWorkflow;

    private bool CanStop() => Node?.IsRunning == true;

    private bool CanCopyPreview() => Preview.Length > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run() => Start(null);

    [RelayCommand(CanExecute = nameof(CanRunInWindow))]
    private void RunInWindow() => Start(ConsoleMode.Window);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (Node is null)
            return;
        var running = main.Output.RunningFor(Node).ToList();
        await Task.WhenAll(running.Select(r => r.StopCommand.ExecuteAsync(null)));
    }

    [RelayCommand(CanExecute = nameof(CanCopyPreview))]
    private void CopyPreview() => main.Services.Shell.CopyText(Preview);

    private bool CanEdit() => Node is { Kind: NodeKind.Script or NodeKind.Workflow, Customisation: null } && !IsEditing;

    private bool CanSave() => IsMyScript && IsRunnable && Form is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => main.MyScripts.SaveValues(Node!, Form!.Snapshot());

    [RelayCommand(CanExecute = nameof(CanSaveAsMyScript))]
    private void SaveAsMyScript() => main.MyScripts.Add(Node!, main.Tree!.MyScriptsRoot);

    private bool CanDuplicate() => IsMyScript && Node!.Node is RunnableNode;

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void Duplicate() => main.MyScripts.DuplicateEntry(Node!);

    private bool CanChangeBase() => Node?.Customisation is { IsCustomisation: true } && ChangeBase is null;

    [RelayCommand(CanExecute = nameof(CanChangeBase))]
    private void OpenChangeBase()
    {
        var picker = new ChangeBaseViewModel(main.MyScripts.BaseChoices(), reference => main.MyScripts.ChangeBase(Node!, reference));
        picker.Closed += () => ChangeBase = null;
        ChangeBase = picker;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit()
    {
        if (Node!.Node is WorkflowNode)
        {
            OpenWorkflowEditor(new WorkflowEditorViewModel(main, Node.Tree, Node));
            return;
        }
        var editor = new ScriptEditorViewModel(main, Node!);
        editor.Closed += _ => Editor = null;
        Editor = editor;
    }

    public void LeaveEditMode()
    {
        Editor = null;
        WorkflowEditor = null;
        ChangeBase = null;
    }

    public void OpenWorkflowEditor(WorkflowEditorViewModel editor)
    {
        editor.Closed += () => WorkflowEditor = null;
        WorkflowEditor = editor;
    }

    /// <summary>Opens through the shell per <see cref="LinkPolicy"/>: executable targets ask first, and never open untrusted.</summary>
    [RelayCommand(CanExecute = nameof(IsLink))]
    private void OpenLink()
    {
        if (Node is not { Node: LinkNode { Url: { Length: > 0 } url } } node)
            return;
        RunError = null;
        var target = LinkTarget(url, node.Tree.BaseDirectory);
        switch (LinkPolicy.Decide(target, main.IsTrusted))
        {
            case LinkAction.Open:
            case LinkAction.Confirm when main.Services.Confirm.Confirm("Open link", $"{target}\n\nThis link starts a program. Open it?"):
                main.Services.Shell.Open(target);
                break;
            case LinkAction.Refuse:
                RunError = "This link starts a program. Trust the workspace to open it.";
                break;
        }
    }

    public static string LinkTarget(string url, string baseDirectory) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme.Length > 1
            ? url
            : Path.GetFullPath(Path.Combine(baseDirectory, url));

    private void Start(ConsoleMode? console)
    {
        if (Node is not { } node)
            return;
        if (BuildWorkflowRequest() is { } workflowRequest)
        {
            StartWorkflow(workflowRequest, node);
            return;
        }
        if (BuildRequest(console) is not { } request)
            return;
        if (request.Script.Confirm is { Length: > 0 } question && !main.Services.Confirm.Confirm(node.Name, question))
            return;
        Launch(request, node);
    }

    private void StartWorkflow(WorkflowRequest request, NodeViewModel node)
    {
        RunError = null;
        var confirming = request.Workflow.Steps.Where(s => s.Confirm == true).Select(s => s.Id ?? s.Run).ToList();
        var question = request.Workflow.Confirm is { Length: > 0 } own ? own
            : confirming.Count > 0 ? $"This workflow runs steps that ask first: {string.Join(", ", confirming)}. Run it?"
            : null;
        if (question is not null && !main.Services.Confirm.Confirm(node.Name, question))
            return;
        try
        {
            var run = main.Services.Workflows.Start(main.Workspace!, request);
            main.Output.Add(new WorkflowRunViewModel(node.Name, node, run, step => StepName(step, request.Tree), main.Services.Dispatcher,
                new StepActions(main.Services.Workflows.StopStepAsync, main.Services.Opener)));
        }
        catch (WorkflowException ex)
        {
            RunError = ex.Message;
        }
    }

    private string StepName(StepRun step, ScriptTree tree) =>
        step.Step.Run is { } reference && main.Workspace?.References.Resolve(reference, tree.Kind) is { } target
            ? ScriptTree.DisplayName(target)
            : step.Id;

    internal void Launch(RunRequest request, NodeViewModel? node)
    {
        RunError = null;
        var title = node?.Name ?? ScriptTree.DisplayName(request.Script);
        RunViewModel run;
        try
        {
            run = new RunViewModel(title, node, main.Services.Launcher.Start(request), main.Services.Dispatcher, ContextFor(request));
        }
        catch (Exception ex) when (IsRunProblem(ex))
        {
            run = RunViewModel.FailedToStart(title, node, ex.Message);
        }
        main.Output.Add(run);
    }

    private RunContext ContextFor(RunRequest request) => new(request, main.Services.Opener, StartCompanion);

    /// <summary>Starts on the caller's thread, since a stop asks from a worker thread; the tab is added on the UI thread.</summary>
    private bool StartCompanion(RunRequest request)
    {
        IRunProcess process;
        try
        {
            process = main.Services.Launcher.Start(request);
        }
        catch (Exception ex) when (IsRunProblem(ex))
        {
            return false;
        }
        main.Services.Dispatcher.Post(() =>
        {
            var node = main.Tree?.AllNodes.FirstOrDefault(n => ReferenceEquals(n.Node, request.Script));
            main.Output.Add(new RunViewModel(node?.Name ?? ScriptTree.DisplayName(request.Script), node, process, main.Services.Dispatcher,
                ContextFor(request)));
        });
        return true;
    }

    private string BuildPreview()
    {
        if (BuildWorkflowRequest() is { } workflow)
            return string.Join(" → ", workflow.Workflow.Steps.Select((step, i) => step.Id ?? $"step{i + 1}"));
        if (BuildRequest() is not { } request)
            return "";
        try
        {
            return string.Join(Environment.NewLine, RunPlanner.Plan(request, main.Services.Interpreters).Select(s => s.Command.Display));
        }
        catch (Exception ex) when (IsRunProblem(ex))
        {
            return $"⚠ {ex.Message}";
        }
    }

    private ParameterFormViewModel? BuildForm()
    {
        if (main.Workspace is not { } workspace || Target is not var (definition, tree) || Node is not { } node)
            return null;
        var stored = main.SessionValues.GetValueOrDefault(node.Key)
            ?? (node.Customisation is { } customisation ? new ParameterValues(customisation.Values, customisation.ExtraArgs ?? "") : null);
        ParameterFormViewModel form;
        try
        {
            form = ParameterFormViewModel.For(workspace, definition, tree, stored, main.Services.Dialogs);
        }
        catch (ArgumentAssemblyException ex)
        {
            RunError = ex.Message;
            return null;
        }
        form.Changed += () =>
        {
            main.SessionValues[node.Key] = form.Snapshot();
            Refresh();
        };
        return form;
    }

    internal static bool IsRunProblem(Exception ex) =>
        ex is RunException or UntrustedWorkspaceException or TemplateException or ArgumentAssemblyException;

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeViewModel.IsRunning))
            StopCommand.NotifyCanExecuteChanged();
    }
}
