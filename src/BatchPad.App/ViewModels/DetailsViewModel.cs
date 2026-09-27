using System.ComponentModel;
using System.Text.Json.Nodes;
using BatchPad.App.Services;
using BatchPad.App.ViewModels.Editor;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.Core.Arguments;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
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
        nameof(EditCommand), nameof(OpenLinkCommand), nameof(SaveAsMyScriptCommand), nameof(DuplicateCommand),
        nameof(OpenChangeBaseCommand), nameof(ReattachCommand))]
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

    private string fullCommand = "";

    [ObservableProperty]
    private string? runError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScriptChoices))]
    private ParameterFormViewModel? form;

    public bool HasScriptChoices => Form?.Fields.Any(f => f.Definition.ChoicesFrom?.Any(s => s.Command is not null) == true) == true;

    [RelayCommand]
    private void RefreshChoices()
    {
        main.CommandChoices.Refresh();
        Form = BuildForm();
        Refresh();
    }

    public string? ValidationMessage => Form?.ErrorSummary;

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
            return main.Tree?.ByDefinition(baseNode) is { } found
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
        (Preview, fullCommand) = BuildPreview();
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
    private void CopyPreview() => main.Services.Shell.CopyText(fullCommand.Length > 0 ? fullCommand : Preview);

    private bool CanEdit() => Node is { Kind: NodeKind.Script or NodeKind.Workflow, Customisation: null } && !IsEditing;

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

    private bool CanReattach() => Node is { IsOrphan: true, ScriptFullPath: not null };

    /// <summary>Points an orphaned entry at another file, keeping its settings (§3.10).</summary>
    [RelayCommand(CanExecute = nameof(CanReattach))]
    private void Reattach()
    {
        var node = Node!;
        var missing = node.ScriptFullPath!;
        var folder = Path.GetDirectoryName(missing);
        if (main.Services.Dialogs.PickFile(Directory.Exists(folder) ? folder : node.Tree.BaseDirectory) is not { } picked)
            return;
        var target = Path.GetFullPath(picked);
        try
        {
            RenameTracker.Move(main.Workspace!, [new FileRename(missing, target)]);
        }
        catch (Exception ex) when (IoProblems.IsIoProblem(ex))
        {
            RunError = ex.Message;
            return;
        }
        main.Reload(n => n.Item is { HasEntry: true } && n.Tree.FilePath == node.Tree.FilePath
            && string.Equals(n.ScriptFullPath, target, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit()
    {
        // An open page hides the details panel, and with it the editor; one holding unsaved edits stays, so nothing opens.
        main.ShowDetails();
        if (main.IsPageOpen)
            return;
        if (Node!.Node is WorkflowNode)
        {
            OpenWorkflowEditor(new WorkflowEditorViewModel(main, Node.Tree, Node));
            return;
        }
        var editor = new ScriptEditorViewModel(main, Node!);
        editor.Closed += _ => Editor = null;
        Editor = editor;
    }

    [RelayCommand]
    private void ShowProposals(NodeViewModel node)
    {
        node.Reveal();
        if (Node != node)
            return;
        if (CanEdit())
            Edit();
        if (Editor is { } editor)
            editor.SelectedTab = ScriptEditorViewModel.ParametersTabIndex;
    }

    internal void ResumeEditing(ScriptEditorViewModel editor)
    {
        editor.Rebind(Node!);
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
                main.Services.Shell.Open(target);
                break;
            case LinkAction.Confirm when main.Services.Confirm.Confirm("Open link", $"{target}\n\nThis link starts a program. Open it?"):
                main.Services.Shell.Open(target, confirmed: true);
                break;
            case LinkAction.Refuse:
                RunError = "This link starts a program. Trust the workspace to open it.";
                break;
        }
    }

    public static string LinkTarget(string url, string baseDirectory) =>
        LinkPolicy.AsUrl(url) is not null ? url : Path.GetFullPath(Path.Combine(baseDirectory, url));

    internal void RunWithSessionValues(NodeViewModel target)
    {
        if (Node == target)
        {
            Form = BuildForm();
            Refresh();
        }
        else
            Node = target;
        if (CanRun())
            Start(null);
    }

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
        if (AskValues(request.Script, request.Tree, request.Values, node) is not { } values)
            return;
        Launch(request with { Values = values }, node);
    }

    /// <returns>Null when the user cancels.</returns>
    private IReadOnlyDictionary<string, JsonNode?>? AskValues(RunnableNode definition, ScriptTree tree,
        IReadOnlyDictionary<string, JsonNode?>? values, NodeViewModel node)
    {
        var prompted = RunPlanner.Prompted(definition, main.Workspace!)
            .Where(p => p.Ask == true || values?.GetValueOrDefault(p.Name!) is null)
            .Select(p => p.Name!)
            .ToHashSet();
        if (prompted.Count == 0)
            return values;

        var stored = main.History.LastValues(node.Key, prompted);
        foreach (var name in prompted)
        {
            if (stored.GetValueOrDefault(name) is null && values?.GetValueOrDefault(name) is { } current)
                stored[name] = current.DeepClone();
        }
        var form = ParameterFormViewModel.For(main.Workspace!, definition, tree, new ParameterValues(stored, ""), main.Services,
            main.CommandChoices, p => prompted.Contains(p.Name!));
        form.HasExtraArguments = false;
        if (!main.Services.Ask.Ask(node.Name, form))
            return null;
        var answered = values?.ToDictionary(v => v.Key, v => v.Value) ?? [];
        foreach (var (name, value) in form.Values)
            answered[name] = value;
        return answered;
    }

    private void StartWorkflow(WorkflowRequest request, NodeViewModel node)
    {
        RunError = null;
        var confirming = request.Workflow.Steps.SelectMany(s => s.Leaves()).Where(s => s.Confirm == true).Select(s => s.Id ?? s.Run).ToList();
        var question = request.Workflow.Confirm is { Length: > 0 } own ? own
            : confirming.Count > 0 ? $"This workflow runs steps that ask first: {string.Join(", ", confirming)}. Run it?"
            : null;
        if (question is not null && !main.Services.Confirm.Confirm(node.Name, question))
            return;
        if (AskValues(request.Workflow, request.Tree, request.Values, node) is not { } values)
            return;
        ShowWorkflow(request with { Values = values }, node);
    }

    private void ShowWorkflow(WorkflowRequest request, NodeViewModel? node)
    {
        try
        {
            var run = main.Services.Workflows.Start(main.Workspace!, request);
            if (request.Target is null)
                main.History.Record(run, node!);
            else
                main.History.RecordSteps(run);
            main.Output.Add(WorkflowTab(node?.Name ?? request.Workflow.Name!, node, run, request.Tree, resumed => ShowWorkflow(resumed, node)));
        }
        catch (WorkflowException ex)
        {
            RunError = ex.Message;
        }
    }

    internal WorkflowRunViewModel WorkflowTab(string title, NodeViewModel? node, WorkflowRun run, ScriptTree tree,
        Action<WorkflowRequest>? rerun = null) =>
        new(title, node, run, step => StepName(step, tree), main.Services.Dispatcher,
            new StepActions(main.Services.Workflows.StopStepAsync, main.Services.Opener, main.Sources), rerun);

    private string StepName(StepRun step, ScriptTree tree) =>
        step.Step.Run is { } reference && main.Workspace?.References.Resolve(reference, tree) is { } target
            ? ScriptTree.DisplayName(target)
            : step.Id;

    internal void Launch(RunRequest request, NodeViewModel? node)
    {
        RunError = null;
        if (Prerequisites.WorkflowFor(request) is { } withPrerequisites)
        {
            ShowWorkflow(withPrerequisites, node);
            return;
        }
        var title = node?.Name ?? ScriptTree.DisplayName(request.Script);
        RunViewModel run;
        try
        {
            var process = main.Services.Launcher.Start(request);
            main.History.Record(process, request, node);
            main.TrackLongRunning(process, request, node);
            run = new RunViewModel(title, node, process, main.Services.Dispatcher, ContextFor(request));
        }
        catch (Exception ex) when (RunProblems.IsRunProblem(ex))
        {
            run = RunViewModel.FailedToStart(title, node, ex.Message);
        }
        main.Output.Add(run);
    }

    internal RunContext ContextFor(RunRequest request) =>
        new(request, main.Services.Opener, StartCompanion, main.Sources, main.RunAgain, path => main.MyScripts.PinLink(path));

    /// <summary>Starts on the caller's thread, since a stop asks from a worker thread; the tab is added on the UI thread.</summary>
    private bool StartCompanion(RunRequest request)
    {
        IRunProcess process;
        try
        {
            process = main.Services.Launcher.Start(request);
            main.History.Record(process, request, null);
        }
        catch (Exception ex) when (RunProblems.IsRunProblem(ex))
        {
            return false;
        }
        main.Services.Dispatcher.Post(() =>
        {
            var node = main.Tree?.ByDefinition(request.Script);
            main.Output.Add(new RunViewModel(node?.Name ?? ScriptTree.DisplayName(request.Script), node, process, main.Services.Dispatcher,
                ContextFor(request)));
        });
        return true;
    }

    private (string Preview, string FullCommand) BuildPreview()
    {
        if (BuildWorkflowRequest() is { } workflow)
            return (string.Join(" → ", workflow.Workflow.Steps.Select((step, i) =>
                step.Parallel is { } members ? $"({string.Join(" | ", members.Select(m => m.Id ?? m.Run))})" : step.Id ?? $"step{i + 1}")), "");
        if (BuildRequest() is not { } request)
            return ("", "");
        try
        {
            var secrets = SecretMasker.SecretValues(request);
            var commands = RunPlanner.Plan(request, main.Services.Interpreters).Select(s => s.Command).ToList();
            var preview = SecretMasker.Mask(
                string.Join(Environment.NewLine, commands.Select(c => c.DisplayRelativeTo(request.Workspace.Directory))), secrets);
            if (Prerequisites.WorkflowFor(request) is { } withPrerequisites)
                preview = string.Join(" → ", withPrerequisites.Workflow.Steps.Select(s =>
                    s.Run is { } reference && request.Workspace.References.Resolve(reference, request.Tree) is { } prerequisite
                        ? ScriptTree.DisplayName(prerequisite)
                        : s.Id ?? s.Run)) + Environment.NewLine + preview;
            return (preview, SecretMasker.Mask(string.Join(Environment.NewLine, commands.Select(c => c.Display)), secrets));
        }
        catch (Exception ex) when (RunProblems.IsRunProblem(ex))
        {
            return ($"⚠ {ex.Message}", "");
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
            form = ParameterFormViewModel.For(workspace, definition, tree, stored, main.Services, main.CommandChoices, p => p.Ask != true);
        }
        catch (ArgumentAssemblyException ex)
        {
            RunError = ex.Message;
            return null;
        }
        form.Changed += () =>
        {
            var values = main.SessionValues[node.Key] = form.Snapshot();
            if (node.IsMyScript && !form.IsFilling)
                main.MyScripts.SaveValues(node, values, form.StoredFieldNames);
            Refresh();
        };
        return form;
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeViewModel.IsRunning))
            StopCommand.NotifyCanExecuteChanged();
    }
}
