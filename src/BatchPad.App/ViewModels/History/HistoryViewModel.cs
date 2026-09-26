using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using BatchPad.Core.History;
using BatchPad.Core.Running;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.History;

public sealed partial class HistoryEntryViewModel(RunRecord record, HistoryViewModel owner)
{
    public RunRecord Record { get; } = record;
    public string Name => Record.Name;
    public string WhenText => Record.StartedAt.ToLocalTime().ToString("g");
    public string DurationText => OutputTabViewModel.FormatDuration(Record.Duration);
    public string ResultText => RunViewModel.StatusOf(new RunResult(Record.Outcome, Record.ExitCode, Record.Duration));
    public bool Succeeded => Record.Succeeded;
    public string Trigger => Record.Trigger;

    [RelayCommand]
    private void OpenLog() => owner.OpenLog(Record);

    [RelayCommand]
    private void RunAgain() => owner.RunAgain(Record);
}

/// <summary>Records the workspace's runs and lists the recent ones (§3.9).</summary>
public sealed partial class HistoryViewModel(MainViewModel main) : ObservableObject
{
    private const int Shown = 200;

    private readonly TimeProvider _time = main.Time;
    private TelemetrySubscription? _telemetry;
    private Action<RunRecord>? _onRecorded;

    public HistoryStore? Store { get; private set; }
    public ObservableCollection<HistoryEntryViewModel> Runs { get; } = [];

    [ObservableProperty]
    private HistoryEntryViewModel? selected;

    public bool Select(string recordId)
    {
        Selected = Runs.FirstOrDefault(r => r.Record.Id == recordId);
        return Selected is not null;
    }

    public void Show(LoadedWorkspace workspace)
    {
        var store = HistoryStore.For(main.Paths, workspace.Id, _time);
        if (Store?.Directory == store.Directory)
            return;
        if (Store is not null)
            Store.RunRecorded -= _onRecorded;
        Store = store;
        _onRecorded = record => OnRecorded(store, record);
        store.RunRecorded += _onRecorded;
        _telemetry?.Dispose();
        _telemetry = main.Telemetry.Attach(store, TelemetryEvents.WorkspaceOf(workspace));
        Runs.Clear();
        foreach (var record in store.Recent(Shown))
            Runs.Add(new HistoryEntryViewModel(record, this));
    }

    public void Record(IRunOutput run, RunRequest request, NodeViewModel? node)
    {
        if (Store is { } store)
            _ = HistoryRecorder.Attach(run, store, request, node?.Key ?? KeyOf(request), name: node?.Name);
    }

    public void Record(WorkflowRun run, NodeViewModel node)
    {
        if (Store is not { } store)
            return;
        var trigger = run.Request.Resume is { } resume ? RunTriggers.ResumedFrom(resume.StepId) : RunTriggers.Manual;
        _ = HistoryRecorder.AttachWorkflow(run, store,
            new RunRecord { NodeKey = node.Key, Tree = node.Tree.Kind, Name = node.Name, Trigger = trigger }, KeyOf, RunTriggers.Manual);
    }

    public void RecordSteps(WorkflowRun run, string trigger = RunTriggers.Manual)
    {
        if (Store is { } store)
            _ = HistoryRecorder.AttachSteps(run, store, KeyOf, trigger);
    }

    /// <summary>The values <paramref name="parameters"/> had in the node's latest run that set each; a masked secret has none.</summary>
    public Dictionary<string, JsonNode?> LastValues(string nodeKey, IEnumerable<string> parameters)
    {
        var found = new Dictionary<string, JsonNode?>();
        var wanted = parameters.ToHashSet();
        foreach (var record in Store?.Recent() ?? [])
        {
            if (wanted.Count == 0)
                break;
            if (record.NodeKey != nodeKey)
                continue;
            foreach (var (name, value) in record.Values)
                if (wanted.Remove(name) && !IsMasked(value))
                    found[name] = value?.DeepClone();
        }
        return found;
    }

    public static bool IsMasked(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<string>(out var text) && text == RunRecord.Masked;

    public string? LastLog(string nodeKey)
    {
        if (Store?.Find(r => r.NodeKey == nodeKey) is not { } record)
            return null;
        try
        {
            return File.ReadAllText(Store.LogPath(record));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void OpenLog(RunRecord record)
    {
        if (Store is not null)
            main.Services.Shell.Open(Store.LogPath(record));
    }

    public void RunAgain(RunRecord record)
    {
        var values = record.Values
            .Where(v => !IsMasked(v.Value))
            .ToDictionary(v => v.Key, v => v.Value?.DeepClone());
        main.RunAgain(record.NodeKey, values, record.ExtraArguments ?? "");
    }

    internal static string KeyOf(RunRequest request) =>
        request.Tree.NodeKey(request.Script, request.Script.Path) ?? $"{request.Tree.Kind}:{request.Script.Name}";

    private void OnRecorded(HistoryStore source, RunRecord record) => main.Services.Dispatcher.Post(() =>
    {
        if (Store != source)
            return;
        Runs.Insert(0, new HistoryEntryViewModel(record, this));
        if (Runs.Count > Shown)
            Runs.RemoveAt(Runs.Count - 1);
    });
}
