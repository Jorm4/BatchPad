using System.Collections.ObjectModel;
using BatchPad.Core.Search;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public sealed record PaletteItem(string Title, string Detail, string Icon, Action Run, NodeViewModel? Node = null)
{
    public string AutomationId => Node?.AutomationId ?? "Command/" + Title;
}

public sealed partial class CommandPaletteViewModel(MainViewModel main) : ObservableObject
{
    private const int MaxResults = 50;
    private List<PaletteItem> _all = [];

    public ObservableCollection<PaletteItem> Results { get; } = [];

    [ObservableProperty]
    private bool isOpen;

    [ObservableProperty]
    private string query = "";

    [ObservableProperty]
    private PaletteItem? selectedItem;

    partial void OnQueryChanged(string value) => Filter();

    [RelayCommand]
    private void Open()
    {
        _all = [.. NodeItems(), .. CommandItems()];
        Query = "";
        Filter();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Runs a script or workflow, opens a link, or carries out a command.</summary>
    [RelayCommand]
    private void Run(PaletteItem? item)
    {
        if ((item ?? SelectedItem) is not { } chosen)
            return;
        IsOpen = false;
        chosen.Run();
    }

    /// <summary>Selects the item in the tree without running it; commands still run.</summary>
    [RelayCommand]
    private void Select(PaletteItem? item)
    {
        if ((item ?? SelectedItem) is not { } chosen)
            return;
        IsOpen = false;
        if (chosen.Node is { } node)
            node.Reveal();
        else
            chosen.Run();
    }

    [RelayCommand]
    private void Move(string? offset)
    {
        if (Results.Count == 0 || !int.TryParse(offset, out var by))
            return;
        var index = SelectedItem is null ? -1 : Results.IndexOf(SelectedItem);
        SelectedItem = Results[Math.Clamp(index + by, 0, Results.Count - 1)];
    }

    private void Filter()
    {
        Results.Clear();
        foreach (var item in FuzzyMatcher.Rank(Query, _all, i => i.Title).Take(MaxResults))
            Results.Add(item);
        SelectedItem = Results.FirstOrDefault();
    }

    private IEnumerable<PaletteItem> NodeItems() =>
        main.Tree is not { } tree ? [] : tree.AllNodes
            .Where(n => n.Kind is NodeKind.Script or NodeKind.Workflow or NodeKind.Link)
            .Select(n => new PaletteItem(n.Name, n.Location, n.Icon, () => RunNode(n), n));

    private IEnumerable<PaletteItem> CommandItems()
    {
        var near = main.SelectedNode ?? main.Tree?.Roots[1];
        if (main.Tree is { } tree)
        {
            yield return new("New script…", "Command", "", () => tree.NewScriptCommand.Execute(near));
            yield return new("New workflow…", "Command", "", () => tree.NewWorkflowCommand.Execute(near));
            yield return new("New link…", "Command", "", () => tree.NewLinkCommand.Execute(near));
        }
        if (main.OpenWorkspaceSettingsCommand.CanExecute(null))
            yield return new("Workspace settings", "Command", "", () => main.OpenWorkspaceSettingsCommand.Execute(null));
        if (main.OpenSchedulesCommand.CanExecute(null))
            yield return new("Schedules", "Command", "", () => main.OpenSchedulesCommand.Execute(null));
        if (main.OpenInsightsCommand.CanExecute(null))
            yield return new("Insights", "Command", "", () => main.OpenInsightsCommand.Execute(null));
        yield return new("New workspace…", "Command", "", () => main.OpenNewWorkspaceCommand.Execute(null));
        yield return new("Settings", "Command", "", () => main.OpenSettingsCommand.Execute(null));
        yield return new("Show history", "Command", "", () => main.Output.IsHistoryOpen = true);
    }

    private void RunNode(NodeViewModel node)
    {
        node.Reveal();
        var command = node.Kind == NodeKind.Link ? main.Details.OpenLinkCommand : main.Details.RunCommand;
        if (command.CanExecute(null))
            command.Execute(null);
    }

}
