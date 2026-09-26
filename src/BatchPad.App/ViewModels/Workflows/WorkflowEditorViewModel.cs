using System.Collections.ObjectModel;
using BatchPad.App.Services;
using BatchPad.App.ViewModels.Editor;
using BatchPad.Core.Arguments;
using BatchPad.Core.Choices;
using BatchPad.Core.Config;
using BatchPad.Core.Customisation;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Workflows;

/// <summary>A workflow parameter chip, e.g. <c>app → Build, Run app</c>.</summary>
public sealed record ParameterChip(string Name, string Receivers)
{
    public string Text => Receivers.Length == 0 ? Name : $"{Name} → {Receivers}";
}

public sealed record StepChoice(string Label, NodeViewModel Node)
{
    public override string ToString() => Label;
}

/// <summary>Edits a copy of a workflow (§4.1, §5.1) as a list of step cards; nothing is written until <see cref="SaveCommand"/>.</summary>
public sealed partial class WorkflowEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ScriptTree _tree;
    private readonly WorkflowNode? _original;
    private readonly FolderNode? _folder;
    private bool _loading = true;

    /// <param name="existing">The workflow node to edit; null for a new workflow.</param>
    /// <param name="folder">Where a new workflow is saved; null for the top level.</param>
    public WorkflowEditorViewModel(MainViewModel main, ScriptTree tree, NodeViewModel? existing, FolderNode? folder = null)
    {
        _main = main;
        _tree = tree;
        _original = existing?.Node as WorkflowNode;
        _folder = folder;
        Workspace = main.Workspace!;
        Definition = _original is null ? new WorkflowNode() : ConfigJson.Clone(_original);
        name = Definition.Name ?? existing?.Name ?? "New workflow";
        description = Definition.Description ?? "";
        id = Definition.Id ?? "";
        nameTemplate = Definition.NameTemplate ?? "";
        Parameters = new ParametersTabViewModel(Definition, Workspace.Workspace.File.SharedParams?.Keys ?? Enumerable.Empty<string>(), [], Refresh);
        ChoiceEnvironment = new ChoiceEnvironment(ChoiceEnvironment.ContextFor(Workspace, tree, Definition, main.CommandChoices),
            main.Services.Dialogs, main.Services.Dispatcher);
        Parameters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ParametersTabViewModel.Selected))
                Choices = BuildChoices();
        };
        choices = BuildChoices();
        StepChoices = BuildStepChoices();
        foreach (var step in Definition.Steps)
            Steps.Add(CardFor(step));
        _loading = false;
        Refresh();
    }

    public LoadedWorkspace Workspace { get; }
    public AppServices Services => _main.Services;
    public CommandChoiceSource CommandChoices => _main.CommandChoices;
    public WorkflowNode Definition { get; }
    public ParametersTabViewModel Parameters { get; }
    public ChoiceEnvironment ChoiceEnvironment { get; }
    public ObservableCollection<StepCardViewModel> Steps { get; } = [];

    /// <summary>Every script step card, members of parallel groups included.</summary>
    public IEnumerable<StepCardViewModel> AllCards => Steps.SelectMany(s => s.IsGroup ? s.Members : [s]);
    public ObservableCollection<ParameterChip> ParameterChips { get; } = [];
    public IReadOnlyList<StepChoice> StepChoices { get; }
    public bool IsShared => _tree.Kind != TreeKind.MyScripts;
    public string Title => _original is null ? "New workflow" : $"Edit — {Name}";

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string description;

    [ObservableProperty]
    private string id;

    [ObservableProperty]
    private string nameTemplate;

    [ObservableProperty]
    private ChoicesTabViewModel? choices;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedStepCommand))]
    private StepChoice? selectedStepChoice;

    [ObservableProperty]
    private bool isParameterEditorOpen;

    [ObservableProperty]
    private string? error;

    public event Action? Closed;

    public IReadOnlyList<string> ParameterNames =>
        Parameters.Items.Select(p => p.Definition.Name ?? p.Use).OfType<string>().ToList();

    public IReadOnlyList<string> ListParameterNames => MergedParameters()
        .Where(p => p.Type == ParameterType.Multichoice || p.Split == true)
        .Select(p => p.Name).OfType<string>().ToList();

    private IEnumerable<ParameterDefinition> MergedParameters()
    {
        try
        {
            return SharedParameters.MergeAll(Parameters.Items.Select(p => p.Definition), Workspace.Workspace.File.SharedParams);
        }
        catch (ArgumentAssemblyException)
        {
            return [];
        }
    }

    public void Refresh()
    {
        if (_loading)
            return;
        ParameterChips.Clear();
        var names = ParameterNames;
        var nameSet = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var received = AllCards.Select(card => (card.Target.Name, Names: card.Receives(nameSet).ToHashSet(StringComparer.OrdinalIgnoreCase))).ToList();
        foreach (var parameter in names)
            ParameterChips.Add(new ParameterChip(parameter, string.Join(", ", received.Where(r => r.Names.Contains(parameter)).Select(r => r.Name))));
    }

    /// <summary>Adds a step running <paramref name="node"/> at <paramref name="index"/> (the end when null). False when it cannot be a step.</summary>
    public bool AddStep(NodeViewModel node, int? index = null)
    {
        if (ReferenceFor(node) is not { } reference)
            return false;
        var ids = AllCards.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var step = new WorkflowStep { Id = IdAssigner.FromName(ReferenceResolver.IdOf(node.Node!) ?? node.Name, ids), Run = reference };
        var card = new StepCardViewModel(this, step, Resolve(reference));
        Steps.Insert(Math.Clamp(index ?? Steps.Count, 0, Steps.Count), card);
        Renumber();
        return true;
    }

    public bool CanAddStep(NodeViewModel node) => ReferenceFor(node) is not null;

    public void MoveStep(StepCardViewModel card, int index)
    {
        if (card.Group is not null)
        {
            Detach(card);
            Steps.Insert(Math.Clamp(index, 0, Steps.Count), card);
            Renumber();
            return;
        }
        var from = Steps.IndexOf(card);
        var to = Math.Clamp(index, 0, Steps.Count - 1);
        if (from < 0 || from == to)
            return;
        Steps.Move(from, to);
        Renumber();
    }

    public void RemoveStep(StepCardViewModel card)
    {
        Detach(card);
        Renumber();
    }

    /// <summary>Runs <paramref name="dropped"/> at the same time as <paramref name="target"/>, joining or making a parallel group (§4.1).</summary>
    public void Group(StepCardViewModel dropped, StepCardViewModel target)
    {
        target = target.Group ?? target;
        if (dropped == target || dropped.Group == target)
            return;
        Detach(dropped);
        var members = dropped.IsGroup ? dropped.Members.ToList() : [dropped];
        if (target.IsGroup)
        {
            foreach (var member in members)
                target.AddMember(member);
        }
        else
        {
            var group = new StepCardViewModel(this, new WorkflowStep { Parallel = [] }, GroupTarget);
            Steps[Steps.IndexOf(target)] = group;
            foreach (var member in members.Prepend(target))
                group.AddMember(member);
        }
        Renumber();
    }

    /// <summary>Adds a step running <paramref name="node"/> to <paramref name="target"/>'s parallel group.</summary>
    public bool GroupWith(NodeViewModel node, StepCardViewModel target)
    {
        if (!AddStep(node))
            return false;
        Group(Steps[^1], target);
        return true;
    }

    public void Ungroup(StepCardViewModel group)
    {
        var index = Steps.IndexOf(group);
        if (!group.IsGroup || index < 0)
            return;
        Steps.RemoveAt(index);
        foreach (var member in group.Members.ToList())
        {
            group.RemoveMember(member);
            Steps.Insert(index++, member);
        }
        Renumber();
    }

    /// <summary>Takes a card out of the list or out of its group; a group left with one member becomes that step.</summary>
    private void Detach(StepCardViewModel card)
    {
        if (card.Group is not { } group)
        {
            Steps.Remove(card);
            return;
        }
        group.RemoveMember(card);
        if (group.Members.Count == 1)
            Ungroup(group);
        else if (group.Members.Count == 0)
            Steps.Remove(group);
    }

    private static readonly StepTarget GroupTarget = new("", null, null, "Parallel", "runs its steps at once");

    private StepCardViewModel CardFor(WorkflowStep step)
    {
        if (step.Parallel is not { } members)
            return new StepCardViewModel(this, step, Resolve(step.Run));
        var group = new StepCardViewModel(this, step, GroupTarget);
        foreach (var member in members)
            group.AddMember(CardFor(member));
        return group;
    }

    private bool CanAddSelectedStep() => SelectedStepChoice is not null;

    [RelayCommand(CanExecute = nameof(CanAddSelectedStep))]
    private void AddSelectedStep()
    {
        AddStep(SelectedStepChoice!.Node);
        SelectedStepChoice = null;
    }

    [RelayCommand]
    private void ToggleParameterEditor() => IsParameterEditorOpen = !IsParameterEditorOpen;

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();

    [RelayCommand]
    private void Save()
    {
        var saved = ConfigJson.Clone(Definition);
        saved.Name = Name.Trim().Length == 0 ? null : Name.Trim();
        saved.Description = GeneralTabViewModel.NullIfEmpty(Description);
        saved.Id = GeneralTabViewModel.NullIfEmpty(Id);
        saved.NameTemplate = GeneralTabViewModel.NullIfEmpty(NameTemplate);
        saved.Steps = Steps.Select(s => s.ToStep()).ToList();
        var folderPath = _folder is null ? null : ConfigEntries.IndexPath(_tree.File.Scripts, _folder);
        try
        {
            ConfigWriter.Update(_tree.FilePath, file =>
            {
                if (_original?.Id is { } id && ConfigEntries.ReplaceWorkflow(file.Scripts, id, saved))
                    return;
                saved.Id ??= IdAssigner.FromName(saved.Name ?? "workflow", ConfigEntries.Ids(file.Scripts, Workspace, _tree));
                ConfigEntries.FolderItems(file.Scripts, folderPath).Add(saved);
            });
        }
        catch (Exception ex) when (IoProblems.IsIoProblem(ex))
        {
            Error = ex.Message;
            return;
        }
        Closed?.Invoke();
        _main.Reload(n => n.Tree.Kind == _tree.Kind && n.Node is WorkflowNode w && w.Id == saved.Id);
    }

    private void Renumber()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Index = i + 1;
            foreach (var member in Steps[i].Members)
                member.Index = i + 1;
        }
        Refresh();
    }

    private ChoicesTabViewModel? BuildChoices() =>
        Parameters.Selected is { IsShared: false } selected ? new ChoicesTabViewModel(selected.Definition, ChoiceEnvironment, Refresh) : null;

    /// <summary>A reference the workflow's tree can resolve: bare ids within My Scripts, else <c>workspace:</c> or <c>global:</c>.</summary>
    private string? ReferenceFor(NodeViewModel node)
    {
        if (node.Node is not RunnableNode { Id: { } id } runnable || ReferenceEquals(runnable, _original))
            return null;
        return node.Tree.Kind switch
        {
            TreeKind.MyScripts => _tree.Kind == TreeKind.MyScripts ? id : null,
            TreeKind.Workspace when _tree.Kind == TreeKind.Global => null,
            _ => ReferenceResolver.Qualified(node.Tree, id),
        };
    }

    private IReadOnlyList<StepChoice> BuildStepChoices() =>
        _main.Tree?.AllNodes.Where(n => n.IsRunnable && ReferenceFor(n) is not null)
            .Select(n => new StepChoice($"{n.Location} › {n.Name}", n)).ToList() ?? [];

    private StepTarget Resolve(string? reference)
    {
        if (reference is null || Workspace.References.Resolve(reference, _tree) is not RunnableNode target)
            return new StepTarget(reference ?? "", null, null, reference ?? "(nothing)", "Missing");
        var tree = Workspace.References.TreeOf(target)!;
        var (definition, definitionTree) = target is ScriptNode { Base: not null } entry
            && new CustomisationResolver(Workspace).Resolve(entry) is { Definition: { } resolved, DefinitionTree: { } resolvedTree }
            ? (resolved, resolvedTree)
            : (target, tree);
        var node = _main.Tree?.ByDefinition(target);
        return new StepTarget(reference, definition, definitionTree, node?.Name ?? ScriptTree.DisplayName(target),
            node is null ? reference : $"{node.Location} › {node.Name}");
    }
}
