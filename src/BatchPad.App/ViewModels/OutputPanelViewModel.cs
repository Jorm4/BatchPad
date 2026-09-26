using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public sealed partial class OutputPanelViewModel : ObservableObject
{
    public ObservableCollection<OutputTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private OutputTabViewModel? selectedTab;

    public void Add(OutputTabViewModel run)
    {
        Tabs.Add(run);
        SelectedTab = run;
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
