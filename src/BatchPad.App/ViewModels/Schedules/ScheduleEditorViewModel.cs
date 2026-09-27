using BatchPad.App.Services;
using System.Globalization;
using System.Text.Json.Nodes;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.Arguments;
using BatchPad.Core.Config;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Schedules;

public sealed record Option<T>(T Value, string Label);

public sealed record ScheduleTargetChoice(string Reference, string Label, string Name, TreeKind Tree);

public sealed partial class ScheduleEditorViewModel : ObservableObject, IUnsavedEdits
{
    private readonly MainViewModel _main;
    private readonly ScheduleEntry? _existing;
    private readonly bool _wasGlobal;
    private readonly List<ScheduleTargetChoice> _allTargets;
    private ScheduleTarget? _target;

    public ScheduleEditorViewModel(MainViewModel main, ScheduleEntry? existing, NodeViewModel? selected)
    {
        _main = main;
        _existing = existing;
        _wasGlobal = existing?.IsGlobal == true;
        var workspace = main.Workspace!;
        _allTargets = main.Tree!.AllNodes
            .Where(n => n is { IsRunnable: true, IsBroken: false, IsOrphan: false, Node: not null })
            .Select(n => workspace.References.QualifiedReference(n.Node!) is { } reference
                ? new ScheduleTargetChoice(reference, $"{n.Location} › {n.Name}", n.Name, n.Tree.Kind)
                : null)
            .OfType<ScheduleTargetChoice>()
            .ToList();

        var schedule = existing?.Schedule;
        var trigger = schedule?.Trigger ?? new Trigger();
        isGlobal = _wasGlobal;
        kind = trigger.Kind is TriggerKind.None ? TriggerKind.Cron : trigger.Kind;
        cron = trigger.Cron ?? "0 2 * * 1-5";
        every = trigger.Every ?? "30m";
        between = trigger.Between ?? "";
        at = trigger.At ?? main.Time.GetLocalNow().Date.AddDays(1).AddHours(9).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        fileChanged = trigger.FileChanged ?? "";
        debounce = trigger.Debounce ?? "";
        afterRun = trigger.AfterRun;
        afterRunResult = trigger.Result ?? AfterRunResult.Always;
        missed = schedule?.Missed ?? MissedPolicy.Skip;
        overlap = schedule?.Overlap ?? OverlapPolicy.Skip;
        runInWindows = schedule?.RunIn == RunIn.Windows;
        Targets = Available();

        var reference = schedule?.Target
            ?? (selected?.Node is { } node ? workspace.References.QualifiedReference(node) : null);
        selectedTarget = Targets.FirstOrDefault(t => t.Reference == reference);
        if (selectedTarget is null && schedule is not null)
            Targets = [.. Targets, selectedTarget = new ScheduleTargetChoice(schedule.Target, schedule.Target, schedule.Target, TreeKind.Workspace)];
        BuildForm();
        _initial = State();
    }

    private readonly string _initial;

    public bool HasUnsavedEdits => State() != _initial;

    public string EditsDescription => _existing is null ? "the new schedule" : "the schedule";

    private string State() => $"{IsGlobal}|" + ConfigJson.Serialize(new Schedule
    {
        Target = SelectedTarget?.Reference ?? "",
        Trigger = BuildTrigger(),
        Values = ChangedValues(),
        Missed = Missed,
        Overlap = Overlap,
        RunIn = RunIn,
    });

    public string Title => _existing is null ? "New schedule" : "Edit schedule";

    public static IReadOnlyList<Option<TriggerKind>> Kinds { get; } =
    [
        new(TriggerKind.Cron, "Cron"), new(TriggerKind.Every, "Every"), new(TriggerKind.At, "Once at"),
        new(TriggerKind.FileChanged, "File changed"), new(TriggerKind.OnStart, "On start"), new(TriggerKind.AfterRun, "After a run"),
    ];

    public static IReadOnlyList<Option<MissedPolicy>> MissedChoices { get; } =
        [new(MissedPolicy.Skip, "Skip runs missed while BatchPad was closed"), new(MissedPolicy.RunOnce, "Run once at start if any were missed")];

    public static IReadOnlyList<Option<OverlapPolicy>> OverlapChoices { get; } =
        [new(OverlapPolicy.Skip, "Skip it"), new(OverlapPolicy.Queue, "Run it after the current run"), new(OverlapPolicy.Parallel, "Run both at once")];

    public static IReadOnlyList<Option<AfterRunResult>> ResultChoices { get; } =
        [new(AfterRunResult.Always, "finishes"), new(AfterRunResult.Success, "succeeds"), new(AfterRunResult.Failure, "fails")];

    [ObservableProperty]
    private IReadOnlyList<ScheduleTargetChoice> targets;

    [ObservableProperty]
    private ScheduleTargetChoice? selectedTarget;

    [ObservableProperty]
    private bool isGlobal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCron), nameof(IsEvery), nameof(IsAt), nameof(IsFileChanged), nameof(IsAfterRun), nameof(IsTimed),
        nameof(RunInWindowsHint))]
    private TriggerKind kind;

    public bool IsCron => Kind == TriggerKind.Cron;
    public bool IsEvery => Kind == TriggerKind.Every;
    public bool IsAt => Kind == TriggerKind.At;
    public bool IsFileChanged => Kind == TriggerKind.FileChanged;
    public bool IsAfterRun => Kind == TriggerKind.AfterRun;
    public bool IsTimed => Kind is TriggerKind.Cron or TriggerKind.Every or TriggerKind.At;

    [ObservableProperty]
    private string cron;

    [ObservableProperty]
    private string every;

    [ObservableProperty]
    private string between;

    [ObservableProperty]
    private string at;

    [ObservableProperty]
    private string fileChanged;

    [ObservableProperty]
    private string debounce;

    [ObservableProperty]
    private string? afterRun;

    [ObservableProperty]
    private AfterRunResult afterRunResult;

    [ObservableProperty]
    private MissedPolicy missed;

    [ObservableProperty]
    private OverlapPolicy overlap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunInApp))]
    private bool runInWindows;

    public bool RunInApp
    {
        get => !RunInWindows;
        set => RunInWindows = !value;
    }

    public RunIn RunIn => RunInWindows ? RunIn.Windows : RunIn.App;

    public string RunInWindowsHint => IsTimed
        ? "Task Scheduler starts each run, also while BatchPad is closed; you need to be signed in to Windows."
        : ScheduleEntry.OnlyTimeTriggersInWindows;

    [ObservableProperty]
    private ParameterFormViewModel? form;

    [ObservableProperty]
    private string? error;

    public string Preview
    {
        get
        {
            var trigger = BuildTrigger();
            if (TriggerMath.Problem(trigger) is { } problem)
                return problem;
            var next = TriggerMath.NextFire(trigger, _main.Time.GetUtcNow(), _main.Time.LocalTimeZone);
            return TriggerText.Describe(trigger) + (next is { } fire ? $" · next {fire.ToLocalTime():g}" : "");
        }
    }

    public event Action? Closed;

    public void SelectTarget(string name) => SelectedTarget = Targets.FirstOrDefault(t => t.Name == name);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Kind) or nameof(Cron) or nameof(Every) or nameof(Between) or nameof(At)
            or nameof(FileChanged) or nameof(Debounce) or nameof(AfterRun) or nameof(AfterRunResult))
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Preview)));
    }

    partial void OnSelectedTargetChanged(ScheduleTargetChoice? value) => BuildForm();

    partial void OnKindChanged(TriggerKind value)
    {
        if (!IsTimed)
            RunInWindows = false;
    }

    partial void OnIsGlobalChanged(bool value)
    {
        Targets = Available();
        if (SelectedTarget is { } current && !Targets.Contains(current))
            SelectedTarget = null;
        else
            BuildForm();
    }

    public Trigger BuildTrigger() => Kind switch
    {
        TriggerKind.Cron => new Trigger { Cron = Cron.Trim() },
        TriggerKind.Every => new Trigger { Every = Every.Trim(), Between = NullIfBlank(Between) },
        TriggerKind.At => new Trigger { At = At.Trim() },
        TriggerKind.FileChanged => new Trigger { FileChanged = FileChanged.Trim(), Debounce = NullIfBlank(Debounce) },
        TriggerKind.OnStart => new Trigger { OnStart = true },
        _ => new Trigger { AfterRun = AfterRun ?? "", Result = AfterRunResult == AfterRunResult.Always ? null : AfterRunResult },
    };

    [RelayCommand]
    private void Save()
    {
        Error = null;
        var trigger = BuildTrigger();
        if (SelectedTarget is null || _target is null)
        {
            Error = SelectedTarget is null ? "Choose what to run." : $"'{SelectedTarget.Reference}' was not found.";
            return;
        }
        var problem = TriggerMath.Problem(trigger)
            ?? (Kind == TriggerKind.AfterRun && string.IsNullOrWhiteSpace(AfterRun) ? "Choose the run to follow." : null);
        if (problem is not null)
        {
            Error = problem;
            return;
        }
        if (Form?.HasErrors == true)
        {
            Error = Form.ErrorSummary;
            return;
        }

        var workspace = _main.Workspace!;
        var values = ChangedValues();
        var hash = DefinitionHash.Of(_target);
        var oldKey = _existing?.Schedule.Key;
        try
        {
            SchedulesViewModel.WriteSchedules(_main, IsGlobal, schedules =>
            {
                var schedule = schedules.FirstOrDefault(s => oldKey is not null && s.Key == oldKey && _wasGlobal == IsGlobal);
                if (schedule is null)
                {
                    schedule = new Schedule { Id = _existing?.Schedule.Id ?? NewId(schedules) };
                    schedules.Add(schedule);
                }
                schedule.Target = SelectedTarget.Reference;
                schedule.Workspace = IsGlobal && (SelectedTarget.Tree == TreeKind.Workspace || RunInWindows) ? workspace.FilePath : null;
                schedule.Trigger = trigger;
                schedule.Values = values;
                schedule.Missed = Missed;
                schedule.Overlap = Overlap;
                schedule.RunIn = RunIn;
                schedule.DefinitionHash = hash;
            });
            if (_existing is not null && _wasGlobal != IsGlobal)
                SchedulesViewModel.WriteSchedules(_main, _wasGlobal, schedules => schedules.RemoveAll(s => s.Key == oldKey));
        }
        catch (Exception ex) when (IoProblems.IsIoProblem(ex))
        {
            Error = ex.Message;
            return;
        }
        Closed?.Invoke();
        _main.Reload();
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();

    private IReadOnlyList<ScheduleTargetChoice> Available() =>
        IsGlobal ? [.. _allTargets.Where(t => t.Tree != TreeKind.MyScripts)] : _allTargets;

    private void BuildForm()
    {
        Error = null;
        Form = null;
        _target = null;
        if (SelectedTarget is null || _main.Workspace is not { } workspace)
            return;
        var from = IsGlobal ? workspace.Global : workspace.MyScripts;
        _target = ScheduleTarget.Resolve(SelectedTarget.Reference, from, workspace, out var problem);
        if (_target is null)
        {
            Error = problem;
            return;
        }
        var stored = new Dictionary<string, JsonNode?>(_target.Values);
        if (_existing?.Schedule.Target == SelectedTarget.Reference)
            foreach (var (name, value) in _existing.Schedule.Values ?? [])
                stored[name] = value?.DeepClone();
        try
        {
            var form = ParameterFormViewModel.For(workspace, _target.Definition, _target.Tree, new ParameterValues(stored, ""),
                _main.Services, _main.CommandChoices, p => p.Type != ParameterType.Secret);
            form.HasExtraArguments = false;
            Form = form;
        }
        catch (ArgumentAssemblyException ex)
        {
            Error = ex.Message;
        }
    }

    private Dictionary<string, JsonNode?>? ChangedValues()
    {
        if (Form is null || _target is null)
            return null;
        var changed = Form.Snapshot().Values
            .Where(v => !(_target.Values.TryGetValue(v.Key, out var inherited) && JsonNode.DeepEquals(inherited, v.Value)))
            .ToDictionary(v => v.Key, v => v.Value?.DeepClone());
        return changed.Count == 0 ? null : changed;
    }

    private string NewId(IEnumerable<Schedule> schedules) =>
        IdAssigner.FromName(SelectedTarget!.Name, schedules.Select(s => s.Id).OfType<string>().ToHashSet(StringComparer.Ordinal));

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
