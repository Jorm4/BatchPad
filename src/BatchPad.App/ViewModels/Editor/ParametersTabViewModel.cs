using System.Collections.ObjectModel;
using BatchPad.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Editor;

public sealed partial class ProposalViewModel(ParameterDefinition parameter, ParametersTabViewModel owner) : ObservableObject
{
    public ParameterDefinition Parameter { get; } = parameter;
    public string Summary => $"{Parameter.Name} · {Parameter.Type?.ToString().ToLowerInvariant()}{(Parameter.Arg is { } arg ? $" · {arg}" : "")}";
    public string? Description => Parameter.Description;

    [RelayCommand]
    private void Accept() => owner.Accept(this);

    [RelayCommand]
    private void Dismiss() => owner.Proposals.Remove(this);
}

public sealed partial class ParametersTabViewModel : ObservableObject
{
    private readonly RunnableNode _definition;
    private readonly Action _changed;

    public ParametersTabViewModel(RunnableNode definition, IEnumerable<string> sharedNames,
        IEnumerable<ParameterDefinition> detected, Action changed)
    {
        _definition = definition;
        _changed = changed;
        SharedNames = sharedNames.Order(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var parameter in definition.Params ?? [])
            Items.Add(Wrap(parameter));
        var taken = Items.Select(i => i.Definition.Name ?? i.Use).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in detected.Where(p => !taken.Contains(p.Name ?? "")))
            Proposals.Add(new ProposalViewModel(parameter, this));
        selected = Items.FirstOrDefault();
    }

    public ObservableCollection<ParameterEditorViewModel> Items { get; } = [];
    public ObservableCollection<ProposalViewModel> Proposals { get; } = [];
    public IReadOnlyList<string> SharedNames { get; }
    public bool HasSharedNames => SharedNames.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private ParameterEditorViewModel? selected;

    /// <summary>Picking a name from the "+ Shared…" box adds a <c>use</c> of it, then the box resets.</summary>
    [ObservableProperty]
    private string? sharedToAdd;

    partial void OnSharedToAddChanged(string? value)
    {
        if (value is null)
            return;
        AddShared(value);
        SharedToAdd = null;
    }

    public ParameterEditorViewModel? Field(string name) =>
        Items.FirstOrDefault(i => string.Equals(i.Definition.Name ?? i.Use, name, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void Add()
    {
        var names = Items.Select(i => i.Definition.Name).ToHashSet();
        var name = Enumerable.Range(1, int.MaxValue).Select(n => $"param{n}").First(n => !names.Contains(n));
        Insert(new ParameterDefinition { Name = name, Type = ParameterType.Text });
    }

    [RelayCommand]
    private void AddShared(string name) => Insert(new ParameterDefinition { Use = name });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        var index = Items.IndexOf(Selected!);
        Items.RemoveAt(index);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        Sync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveUp() => Move(Selected!, -1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveDown() => Move(Selected!, 1);

    public void Move(ParameterEditorViewModel item, int offset)
    {
        var from = Items.IndexOf(item);
        var to = Math.Clamp(from + offset, 0, Items.Count - 1);
        if (from < 0 || to == from)
            return;
        Items.Move(from, to);
        Sync();
    }

    internal void Accept(ProposalViewModel proposal)
    {
        Proposals.Remove(proposal);
        Insert(proposal.Parameter);
    }

    private bool HasSelection() => Selected is not null;

    private void Insert(ParameterDefinition parameter)
    {
        var item = Wrap(parameter);
        Items.Add(item);
        Selected = item;
        Sync();
    }

    private ParameterEditorViewModel Wrap(ParameterDefinition parameter) => new(parameter, _changed);

    private void Sync()
    {
        _definition.Params = Items.Count == 0 ? null : Items.Select(i => i.Definition).ToList();
        _changed();
    }
}
