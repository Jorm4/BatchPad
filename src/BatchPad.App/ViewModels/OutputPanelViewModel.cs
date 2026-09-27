using System.Collections.ObjectModel;
using BatchPad.App.ViewModels.History;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public sealed partial class OutputPanelViewModel(HistoryViewModel history) : ObservableObject
{
    public ObservableCollection<OutputTabViewModel> Tabs { get; } = [];
    public HistoryViewModel History { get; } = history;

    [ObservableProperty]
    private OutputTabViewModel? selectedTab;

    [ObservableProperty]
    private bool isHistoryOpen;

    public void Add(OutputTabViewModel run)
    {
        Tabs.Add(run);
        SelectedTab = run;
        IsHistoryOpen = false;
    }

    partial void OnSelectedTabChanged(OutputTabViewModel? value)
    {
        if (value is not null)
            IsHistoryOpen = false;
    }

    /// <summary>Shows the node's running tab, else its latest; a node without one leaves the panel as it is.</summary>
    public void ShowLatestFor(NodeViewModel node)
    {
        // By key: a reload rebuilds the tree, while tabs keep the node they were started from.
        var tabs = Tabs.Where(t => t.Node?.Key == node.Key).ToList();
        if ((tabs.LastOrDefault(t => t.IsRunning) ?? tabs.LastOrDefault()) is { } tab)
            SelectedTab = tab;
    }

    public IEnumerable<OutputTabViewModel> RunningFor(NodeViewModel node) => Tabs.Where(t => t.Node == node && t.IsRunning);

    [RelayCommand]
    private void CloseTab(OutputTabViewModel? run)
    {
        if (run is null || run.IsRunning)
            return;
        var index = Tabs.IndexOf(run);
        Tabs.Remove(run);
        run.Dispose();
        if (SelectedTab is null && Tabs.Count > 0)
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }
}
