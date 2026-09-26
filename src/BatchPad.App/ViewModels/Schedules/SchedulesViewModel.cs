using BatchPad.App.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using BatchPad.Core.Config;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Schedules;

public sealed partial class ScheduleItemViewModel : ObservableObject
{
    private readonly SchedulesViewModel _owner;
    private bool _loading = true;

    public ScheduleItemViewModel(SchedulesViewModel owner, ScheduleEntry entry)
    {
        _owner = owner;
        Entry = entry;
        isEnabled = entry.Schedule.Enabled;
        _loading = false;
    }

    public ScheduleEntry Entry { get; }
    public Schedule Schedule => Entry.Schedule;
    public string Key => Entry.Key;
    public bool IsGlobal => Entry.IsGlobal;
    public string TargetName => Entry.Target?.Name ?? Schedule.Target;
    public string Location => IsGlobal ? "global.json" : "user.json";
    public string TriggerText => BatchPad.Core.Scheduling.TriggerText.Describe(Schedule.Trigger);

    public bool IsLastRun(RunRecord record) => record.Trigger == RunTriggers.Schedule(Key) && record.NodeKey == Entry.Target?.NodeKey;

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunText))]
    private DateTimeOffset? nextRun;

    public string NextRunText => NextRun is { } next ? next.ToLocalTime().ToString("g") : "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastRunText), nameof(LastResultText), nameof(LastSucceeded))]
    private RunRecord? lastRun;

    public string LastRunText => LastRun is { } last ? last.StartedAt.ToLocalTime().ToString("g") : "never";
    public string LastResultText => LastRun is { } last ? RunViewModel.StatusOf(new RunResult(last.Outcome, last.ExitCode, last.Duration)) : "";
    public bool LastSucceeded => LastRun?.Succeeded == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool needsReview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? message;

    public string? StatusText =>
        Entry.Problem ?? Message ?? (NeedsReview ? "Definition changed — review" : null);

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading)
            _owner.SetEnabled(this, value);
    }

    [RelayCommand]
    private void RunNow() => _owner.RunNow(this);

    [RelayCommand]
    private void Reconfirm() => _owner.Reconfirm(this);

    [RelayCommand]
    private void Edit() => _owner.OpenEditor(this);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);
}

/// <summary>The Schedules page (§4.2): every schedule of the workspace with its next and last run.</summary>
public sealed partial class SchedulesViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SchedulesViewModel(MainViewModel main)
    {
        _main = main;
        _main.History.Runs.CollectionChanged += OnHistoryChanged;
        Refresh();
    }

    public ObservableCollection<ScheduleItemViewModel> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public bool KeepRunningInTray
    {
        get => _main.KeepRunningInTray;
        set => _main.KeepRunningInTray = value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen))]
    private ScheduleEditorViewModel? editor;

    public bool IsEditorOpen => Editor is not null;

    [ObservableProperty]
    private string? error;

    public event Action? Closed;

    public ScheduleItemViewModel? Item(string targetName) => Items.FirstOrDefault(i => i.TargetName == targetName);

    /// <summary>Rebuilds the list from the loaded workspace; an open editor is dropped, since it holds the old trees.</summary>
    public void Refresh(IReadOnlyList<ScheduleEntry>? entries = null)
    {
        Editor = null;
        Items.Clear();
        if (_main.Workspace is { } workspace)
            foreach (var entry in entries ?? ScheduleEntry.For(workspace))
                Items.Add(new ScheduleItemViewModel(this, entry));
        RefreshStatus();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void RefreshStatus()
    {
        ShowStatuses();
        var recent = _main.History.Store?.Recent() ?? [];
        foreach (var item in Items)
            item.LastRun = recent.FirstOrDefault(item.IsLastRun);
    }

    private void ShowStatuses()
    {
        var statuses = _main.Scheduler?.Statuses().ToDictionary(s => s.Entry.Key) ?? [];
        foreach (var item in Items)
        {
            var status = statuses.GetValueOrDefault(item.Key);
            item.NextRun = status?.NextFire;
            item.NeedsReview = status?.Paused == true;
        }
    }

    public void ShowFailure(ScheduleFailure failure)
    {
        if (Items.FirstOrDefault(i => i.Key == failure.Entry.Key) is { } item)
            item.Message = failure.Message;
        RefreshStatus();
    }

    [RelayCommand]
    private void Add() => OpenEditor(null);

    [RelayCommand]
    private void Close() => Closed?.Invoke();

    public void Detach() => _main.History.Runs.CollectionChanged -= OnHistoryChanged;

    internal void OpenEditor(ScheduleItemViewModel? item)
    {
        var editor = new ScheduleEditorViewModel(_main, item?.Entry, _main.SelectedNode);
        editor.Closed += () => Editor = null;
        Editor = editor;
    }

    internal void RunNow(ScheduleItemViewModel item)
    {
        item.Message = null;
        if (_main.Scheduler is not { } scheduler)
            return;
        if (!scheduler.RunNow(item.Key) && item.Message is null)
            item.Message = item.NeedsReview ? "Re-confirm the changed definition first." : "Already running.";
        RefreshStatus();
    }

    internal void Reconfirm(ScheduleItemViewModel item)
    {
        if (_main.Scheduler?.Confirm(item.Key) is not { } hash)
            return;
        Save(item, schedule => schedule.DefinitionHash = hash);
    }

    internal void SetEnabled(ScheduleItemViewModel item, bool enabled) => Save(item, schedule => schedule.Enabled = enabled);

    internal void Remove(ScheduleItemViewModel item)
    {
        if (!_main.Services.Confirm.Confirm("Remove schedule", $"Remove the schedule for {item.TargetName} ({item.TriggerText})?"))
            return;
        Write(item.IsGlobal, schedules => schedules.RemoveAll(s => s.Key == item.Schedule.Key));
    }

    private void Save(ScheduleItemViewModel item, Action<Schedule> change) =>
        Write(item.IsGlobal, schedules =>
        {
            if (schedules.FirstOrDefault(s => s.Key == item.Schedule.Key) is { } saved)
                change(saved);
        });

    private void Write(bool global, Action<List<Schedule>> change)
    {
        Error = null;
        if (_main.Workspace is null)
            return;
        try
        {
            WriteSchedules(_main, global, change);
        }
        catch (Exception ex) when (IoProblems.IsIoProblem(ex))
        {
            Error = ex.Message;
            return;
        }
        _main.Reload();
    }

    internal static void WriteSchedules(MainViewModel main, bool global, Action<List<Schedule>> change) =>
        ConfigWriter.Update(global ? main.Paths.GlobalFile : main.Workspace!.MyScripts.FilePath, file =>
        {
            var schedules = file.Schedules ?? [];
            change(schedules);
            file.Schedules = schedules.Count == 0 ? null : schedules;
        });

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ShowStatuses();
        foreach (var record in (e.NewItems ?? Array.Empty<object>()).OfType<History.HistoryEntryViewModel>().Select(h => h.Record))
            foreach (var item in Items.Where(i => i.IsLastRun(record)))
                item.LastRun = record;
    }
}
