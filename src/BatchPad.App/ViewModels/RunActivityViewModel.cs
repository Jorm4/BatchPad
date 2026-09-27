using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Shell;
using BatchPad.App.Services;
using BatchPad.App.ViewModels.Workflows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchPad.App.ViewModels;

/// <summary>The taskbar button's progress, and a notification when a long run finishes while the window is inactive.</summary>
public sealed partial class RunActivityViewModel : ObservableObject
{
    public static readonly TimeSpan NotifyAfter = TimeSpan.FromSeconds(10);

    private readonly MainViewModel _main;
    private readonly Dictionary<OutputTabViewModel, long> _started = [];
    private IWindowActivity? _window;
    private bool _failedWhileInactive;

    [ObservableProperty]
    private TaskbarItemProgressState taskbarState;

    [ObservableProperty]
    private double taskbarProgress;

    public RunActivityViewModel(MainViewModel main)
    {
        _main = main;
        main.Output.Tabs.CollectionChanged += OnTabsChanged;
    }

    public IWindowActivity? Window
    {
        get => _window;
        set
        {
            if (_window is not null)
                _window.Activated -= OnActivated;
            _window = value;
            if (value is not null)
                value.Activated += OnActivated;
        }
    }

    private bool IsInactive => _window is { IsActive: false };

    private void OnActivated()
    {
        _failedWhileInactive = false;
        Refresh();
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tab in e.OldItems?.OfType<OutputTabViewModel>() ?? [])
        {
            tab.PropertyChanged -= OnTabChanged;
            _started.Remove(tab);
        }
        foreach (var tab in e.NewItems?.OfType<OutputTabViewModel>() ?? [])
        {
            tab.PropertyChanged += OnTabChanged;
            if (tab.IsRunning)
                _started[tab] = _main.Time.GetTimestamp();
        }
        Refresh();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not OutputTabViewModel tab)
            return;
        if (e.PropertyName == nameof(OutputTabViewModel.IsRunning) && !tab.IsRunning && _started.Remove(tab, out var start))
            OnFinished(tab, _main.Time.GetElapsedTime(start));
        if (e.PropertyName is nameof(OutputTabViewModel.IsRunning) or nameof(RunViewModel.IsReady) or nameof(WorkflowRunViewModel.FinishedSteps))
            Refresh();
    }

    private void OnFinished(OutputTabViewModel tab, TimeSpan elapsed)
    {
        if (!IsInactive)
            return;
        if (!tab.Succeeded)
            _failedWhileInactive = true;
        // A failed scheduled run already has the scheduler's notification.
        if (elapsed <= NotifyAfter || (tab.IsScheduled && !tab.Succeeded))
            return;
        var outcome = tab is RunViewModel ? $"{(tab.Succeeded ? "succeeded" : "failed")}, {tab.StatusText}" : tab.StatusText;
        _main.Tray?.Notify(tab.Title, $"{outcome}, {OutputTabViewModel.FormatDuration(elapsed)}",
            tab.Succeeded ? NotificationSeverity.Info : NotificationSeverity.Error, () => Show(tab));
    }

    private void Show(OutputTabViewModel tab)
    {
        _main.ShowWindow();
        if (_main.Output.Tabs.Contains(tab))
            _main.Output.SelectedTab = tab;
    }

    private void Refresh()
    {
        var running = _main.Output.Tabs.Where(t => t.IsRunning && t is not RunViewModel { IsReady: true }).ToList();
        (TaskbarState, TaskbarProgress) = _failedWhileInactive ? (TaskbarItemProgressState.Error, 1.0)
            : running switch
            {
                [] => (TaskbarItemProgressState.None, 0.0),
                [WorkflowRunViewModel { StepCount: > 0 } workflow] =>
                    (TaskbarItemProgressState.Normal, (double)workflow.FinishedSteps / workflow.StepCount),
                _ => (TaskbarItemProgressState.Indeterminate, 0.0),
            };
    }
}
