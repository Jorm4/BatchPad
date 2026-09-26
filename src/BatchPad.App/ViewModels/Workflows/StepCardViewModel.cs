using BatchPad.Core.Config;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using BatchPad.App.ViewModels.Editor;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Workflows;

public sealed record WhenOption(StepWhen Value, string Label)
{
    public static IReadOnlyList<WhenOption> All { get; } =
    [
        new(StepWhen.Success, "If all passed"),
        new(StepWhen.Failure, "If any failed"),
        new(StepWhen.Always, "Always"),
    ];

    public override string ToString() => Label;
}

public sealed record StepBinding(string Parameter, string Template)
{
    public string Text => $"{Parameter} ← {Template}";
}

/// <summary>What a step runs, resolved from its <c>run</c> reference.</summary>
public sealed record StepTarget(string Reference, RunnableNode? Definition, ScriptTree? Tree, string Name, string Location);

/// <summary>
/// One step card of the workflow editor (§4.1, §5.1): its values in the generated form, for-each, <c>when</c>, flow chips and run
/// options; or a parallel group card holding member cards.
/// </summary>
public sealed partial class StepCardViewModel : ObservableObject
{
    private const string ItemValue = "${item}";

    private readonly WorkflowEditorViewModel _owner;
    private readonly WorkflowStep _step;
    private readonly Dictionary<string, string> _bindings;

    public StepCardViewModel(WorkflowEditorViewModel owner, WorkflowStep step, StepTarget target)
    {
        _owner = owner;
        _step = step;
        Target = target;
        _bindings = (step.Values ?? []).Where(v => TemplateText(v.Value) is not null)
            .ToDictionary(v => v.Key, v => TemplateText(v.Value)!);
        TargetParameters = Parameters(target, owner.Workspace);
        id = step.Id ?? "";
        emptyArgs = step.EmptyArgs is { } args ? ArgvQuoter.Join(args) : "";
        when = WhenOption.All.First(o => o.Value == (step.When ?? StepWhen.Success));
        IsGroup = step.Parallel is not null;
        continueOnError = step.ContinueOnError == true;
        failFast = step.FailFast == true;
        confirm = step.Confirm == true;
        parallelDegree = step.MaxParallel?.ToString(CultureInfo.InvariantCulture) ?? "";
        retryCount = step.Retry is { Count: > 0 } retry ? retry.Count.ToString(CultureInfo.InvariantCulture) : "";
        retryDelay = step.Retry is { DelaySeconds: > 0 } delayed ? delayed.DelaySeconds.ToString(CultureInfo.InvariantCulture) : "";
        if (step.ForEach is { } forEach && ParamName(forEach) is { } listName)
        {
            isForEach = true;
            forEachParameter = listName;
            itemTarget = _bindings.FirstOrDefault(b => b.Value == ItemValue).Key;
        }
        form = BuildForm(step.Values);
    }

    [ObservableProperty]
    private string id;

    [ObservableProperty]
    private string emptyArgs;

    public bool IsGroup { get; }
    public ObservableCollection<StepCardViewModel> Members { get; } = [];

    public StepCardViewModel? Group { get; private set; }

    public bool IsMember => Group is not null;

    [ObservableProperty]
    private bool continueOnError;

    [ObservableProperty]
    private bool failFast;

    [ObservableProperty]
    private bool confirm;

    /// <summary>How many <c>forEach</c> items run at once; empty for one at a time.</summary>
    [ObservableProperty]
    private string parallelDegree;

    [ObservableProperty]
    private string retryCount;

    [ObservableProperty]
    private string retryDelay;
    public StepTarget Target { get; }
    public IReadOnlyList<ParameterDefinition> TargetParameters { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private int index;

    public string Title => $"{Index} {Target.Name}";

    [ObservableProperty]
    private ParameterFormViewModel? form;

    public IReadOnlyList<WhenOption> WhenOptions => WhenOption.All;

    [ObservableProperty]
    private WhenOption when;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForEachText))]
    private bool isForEach;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForEachText))]
    private string? forEachParameter;

    /// <summary>The step parameter that receives each item.</summary>
    [ObservableProperty]
    private string? itemTarget;

    public IReadOnlyList<string> ForEachOptions => _owner.ListParameterNames;
    public IReadOnlyList<string> ItemTargetOptions => TargetParameters.Select(p => p.Name!).ToList();

    public IReadOnlyList<StepBinding> Bindings =>
        _bindings.Where(b => b.Value != ItemValue).Select(b => new StepBinding(b.Key, b.Value)).ToList();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddBindingCommand))]
    private string? bindingTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddBindingCommand))]
    private string bindingTemplate = "";

    public string ForEachText => $"For each {ForEachParameter} — run '{Target.Name}' once per item";

    /// <summary>For example <c>apps ← game</c>: explicit bindings, then parameters that flow in by name.</summary>
    public IReadOnlyList<string> FlowChips =>
    [
        .. _bindings.Select(b => $"{b.Key} ← {(b.Value == ItemValue ? $"each {ForEachParameter}" : ParamName(b.Value) ?? b.Value)}"),
        .. ImplicitFlow(_owner.ParameterNames.ToHashSet(StringComparer.OrdinalIgnoreCase)).Select(name => $"{name} ← {name}"),
    ];

    public IEnumerable<string> Receives(IReadOnlySet<string> workflowParameters) =>
        ImplicitFlow(workflowParameters)
            .Concat(_bindings.Values.Select(ParamName).OfType<string>())
            .Concat(IsForEach && ForEachParameter is { } list ? [list] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase);

    partial void OnWhenChanged(WhenOption value) => _owner.Refresh();

    partial void OnIsForEachChanged(bool value)
    {
        if (value)
        {
            ForEachParameter ??= ForEachOptions.FirstOrDefault();
            ItemTarget ??= TargetParameters.FirstOrDefault(p => string.Equals(p.Name, ForEachParameter, StringComparison.OrdinalIgnoreCase))?.Name
                ?? TargetParameters.FirstOrDefault()?.Name;
        }
        BindItem();
    }

    partial void OnForEachParameterChanged(string? value) => Changed();

    partial void OnItemTargetChanged(string? oldValue, string? newValue) => BindItem();

    private bool CanAddBinding() => BindingTarget is not null && BindingTemplate.Contains("${", StringComparison.Ordinal);

    [RelayCommand(CanExecute = nameof(CanAddBinding))]
    private void AddBinding()
    {
        _bindings[BindingTarget!] = BindingTemplate.Trim();
        BindingTarget = null;
        BindingTemplate = "";
        Rebind();
    }

    [RelayCommand]
    private void RemoveBinding(StepBinding binding)
    {
        _bindings.Remove(binding.Parameter);
        Rebind();
    }

    [RelayCommand]
    private void Remove() => _owner.RemoveStep(this);

    [RelayCommand]
    private void Ungroup() => _owner.Ungroup(this);

    internal void AddMember(StepCardViewModel member)
    {
        member.Group = this;
        member.OnPropertyChanged(nameof(IsMember));
        Members.Add(member);
    }

    internal void RemoveMember(StepCardViewModel member)
    {
        member.Group = null;
        member.OnPropertyChanged(nameof(IsMember));
        Members.Remove(member);
    }

    [RelayCommand]
    private void MoveUp() => _owner.MoveStep(this, Index - 2);

    [RelayCommand]
    private void MoveDown() => _owner.MoveStep(this, Index);

    public WorkflowStep ToStep()
    {
        var step = ConfigJson.Clone(_step);
        step.Id = GeneralTabViewModel.NullIfEmpty(Id) ?? _step.Id;
        step.ContinueOnError = ContinueOnError ? true : null;
        if (IsGroup)
        {
            step.Parallel = [.. Members.Select(m => m.ToStep())];
            return step;
        }
        step.Confirm = Confirm ? true : null;
        step.FailFast = IsForEach && FailFast ? true : null;
        step.MaxParallel = IsForEach && int.TryParse(ParallelDegree, CultureInfo.InvariantCulture, out var degree) && degree > 1 ? degree : null;
        if (int.TryParse(RetryCount, CultureInfo.InvariantCulture, out var count) && count > 0)
        {
            step.Retry ??= new StepRetry();
            step.Retry.Count = count;
            step.Retry.DelaySeconds = double.TryParse(RetryDelay, CultureInfo.InvariantCulture, out var delay) ? Math.Max(0, delay) : 0;
        }
        else
            step.Retry = null;
        step.EmptyArgs = ParameterEditorViewModel.SplitOrNull(EmptyArgs);
        var values = Form?.Values.ToDictionary(v => v.Key, v => v.Value?.DeepClone()) ?? [];
        foreach (var (name, template) in _bindings)
            values[name] = BindingValue(name, template);
        step.Values = values.Count == 0 ? null : values;
        step.When = When.Value == StepWhen.Success ? null : When.Value;
        step.ForEach = IsForEach && ForEachParameter is { } list ? $"${{param:{list}}}" : null;
        return step;
    }

    private void BindItem()
    {
        foreach (var bound in _bindings.Where(b => b.Value == ItemValue).Select(b => b.Key).ToList())
            _bindings.Remove(bound);
        if (IsForEach && ItemTarget is { } target)
            _bindings[target] = ItemValue;
        Rebind();
    }

    private void Rebind()
    {
        Form = BuildForm(Form?.Values);
        OnPropertyChanged(nameof(Bindings));
        Changed();
    }

    /// <summary>A single value bound to a multichoice is written as a one-item list, as the target expects.</summary>
    private JsonNode BindingValue(string parameter, string template)
    {
        var isListTarget = TargetParameters.Any(p => p.Type == ParameterType.Multichoice
            && string.Equals(p.Name, parameter, StringComparison.OrdinalIgnoreCase));
        var isListSource = ParamName(template) is { } source && _owner.ListParameterNames.Contains(source, StringComparer.OrdinalIgnoreCase);
        return isListTarget && !isListSource && template != ItemValue ? new JsonArray(JsonValue.Create(template)) : JsonValue.Create(template);
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(FlowChips));
        _owner.Refresh();
    }

    private IEnumerable<string> ImplicitFlow(IReadOnlySet<string> workflowParameters)
    {
        var set = Form?.Values.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return TargetParameters.Select(p => p.Name!)
            .Where(name => !_bindings.ContainsKey(name) && !set.Contains(name) && workflowParameters.Contains(name));
    }

    private ParameterFormViewModel? BuildForm(IReadOnlyDictionary<string, JsonNode?>? values)
    {
        if (Target is not { Definition: { } definition, Tree: { } tree })
            return null;
        var stored = new ParameterValues(
            (values ?? new Dictionary<string, JsonNode?>()).Where(v => !_bindings.ContainsKey(v.Key)).ToDictionary(v => v.Key, v => v.Value), "");
        try
        {
            var built = ParameterFormViewModel.For(_owner.Workspace, definition, tree, stored, _owner.Services, _owner.CommandChoices,
                p => p.Type != ParameterType.Secret && !_bindings.ContainsKey(p.Name!));
            built.Changed += Changed;
            return built;
        }
        catch (ArgumentAssemblyException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ParameterDefinition> Parameters(StepTarget target, LoadedWorkspace workspace)
    {
        try
        {
            return target.Definition is { } definition
                ? SharedParameters.MergeAll(definition.Params, workspace.Workspace.File.SharedParams).Where(p => p.Name is not null).ToList()
                : [];
        }
        catch (ArgumentAssemblyException)
        {
            return [];
        }
    }

    private static string? TemplateText(JsonNode? value) => value switch
    {
        JsonValue json when json.TryGetValue<string>(out var text) && text.Contains("${") => text,
        JsonArray { Count: 1 } list => TemplateText(list[0]),
        _ => null,
    };

    private static string? ParamName(string template) =>
        template.StartsWith("${param:", StringComparison.Ordinal) && template.EndsWith('}') && template.IndexOf('}') == template.Length - 1
            ? template[8..^1]
            : null;
}
