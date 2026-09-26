using BatchPad.Core.Config;
using System.Collections.ObjectModel;
using System.Text.Json;
using BatchPad.App.Services;
using BatchPad.Core.Choices;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Editor;

public sealed record ChoicePreviewRow(string Label, string Value);

/// <summary>What the choice editors need from their surroundings: where paths are relative to, and how to resolve.</summary>
public sealed class ChoiceEnvironment(ChoiceContext context, IFileDialogService dialogs, IUiDispatcher dispatcher)
{
    private readonly ChoiceResolver _resolver = new();

    public string BaseDirectory => context.BaseDirectory;
    public CommandChoiceSource? Commands => context.Commands;
    public IFileDialogService Dialogs => dialogs;
    public IUiDispatcher Dispatcher => dispatcher;

    public ResolvedChoices Resolve(ParameterDefinition parameter) => _resolver.Resolve(parameter, context);

    /// <summary>Where a definition's choices resolve, as a run resolves them.</summary>
    public static ChoiceContext ContextFor(LoadedWorkspace workspace, ScriptTree tree, RunnableNode? definition, CommandChoiceSource? commands) =>
        (definition is ScriptNode script
            ? RunPlanner.ChoicesFor(new RunRequest(workspace, tree, script))
            : new ChoiceContext(workspace.Directory)
            {
                Lists = workspace.Workspace.File.Lists,
                Templates = new TemplateContext { WorkspaceDir = workspace.Directory, Variables = workspace.Workspace.File.Variables },
                Paths = tree.Paths,
            }) with { Commands = commands };
}

/// <summary>A resolved preview of choices, refreshed off the UI thread once edits pause.</summary>
public abstract partial class ChoicePreviewViewModel(ChoiceEnvironment environment) : ObservableObject
{
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(300);

    private readonly Debouncer _preview = new(environment.Dispatcher, PreviewDelay);

    public ObservableCollection<ChoicePreviewRow> Preview { get; } = [];

    [ObservableProperty]
    private string? problems;

    protected void ShowPreview(ParameterDefinition parameter)
    {
        var copy = ConfigJson.Clone(parameter);
        _preview.Run(() => environment.Resolve(copy), resolved =>
        {
            Preview.Clear();
            foreach (var choice in resolved.Choices)
                Preview.Add(new ChoicePreviewRow(choice.DisplayLabel, choice.Value));
            Problems = resolved.Problems.Count == 0 ? null : string.Join(Environment.NewLine, resolved.Problems);
        });
    }
}

public sealed partial class ChoiceFieldCell(string name, string value, Action changed) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private string value = value;

    partial void OnValueChanged(string value) => changed();
}

public sealed partial class ChoiceRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public ChoiceRowViewModel(string label, string value, bool split, IEnumerable<ChoiceFieldCell> fields, Action changed)
    {
        this.label = label;
        this.value = value;
        this.split = split;
        _changed = changed;
        Fields = new ObservableCollection<ChoiceFieldCell>(fields);
    }

    public ObservableCollection<ChoiceFieldCell> Fields { get; }

    [ObservableProperty]
    private string label;

    [ObservableProperty]
    private string value;

    [ObservableProperty]
    private bool split;

    partial void OnLabelChanged(string value) => _changed();
    partial void OnValueChanged(string value) => _changed();
    partial void OnSplitChanged(bool value) => _changed();

    /// <summary>Every field is written, empty ones too, since a template naming a missing field fails.</summary>
    public ChoiceDefinition ToDefinition()
    {
        var fields = Fields.ToDictionary(f => f.Name, f => JsonSerializer.SerializeToElement(f.Value));
        return new ChoiceDefinition
        {
            Value = Value,
            Label = Label.Length == 0 || (Label == Value && fields.Count == 0 && !Split) ? null : Label,
            Split = Split,
            Fields = fields,
        };
    }
}

public sealed partial class ChoiceSourceRowViewModel(ChoiceSource source, ChoicesTabViewModel owner) : ObservableObject
{
    public ChoiceSource Source { get; } = source;
    public string Summary => ChoiceSourcePickerViewModel.Describe(Source);

    [RelayCommand]
    private void Edit() => owner.OpenPicker(this);

    [RelayCommand]
    private void Remove() => owner.RemoveSource(this);
}

/// <summary>Edits a choice parameter's fixed rows and its <c>choicesFrom</c> sources (§5.1), writing into the definition.</summary>
public sealed partial class ChoicesTabViewModel : ChoicePreviewViewModel
{
    private readonly ParameterDefinition _parameter;
    private readonly ChoiceEnvironment _environment;
    private readonly Action _changed;
    private ChoiceSourceRowViewModel? _editing;

    public ChoicesTabViewModel(ParameterDefinition parameter, ChoiceEnvironment environment, Action changed)
        : base(environment)
    {
        _parameter = parameter;
        _environment = environment;
        _changed = changed;
        foreach (var name in (parameter.Choices ?? []).SelectMany(c => c.Fields.Keys).Distinct())
            FieldNames.Add(name);
        foreach (var choice in parameter.Choices ?? [])
            Rows.Add(NewRow(choice.Label ?? choice.Value, choice.Value, choice.Split, choice.Fields));
        foreach (var source in parameter.ChoicesFrom ?? [])
            Sources.Add(new ChoiceSourceRowViewModel(source, this));
        lowercaseValues = parameter.ValueTransform == "lower";
        RefreshPreview();
    }

    public ObservableCollection<string> FieldNames { get; } = [];
    public ObservableCollection<ChoiceRowViewModel> Rows { get; } = [];
    public ObservableCollection<ChoiceSourceRowViewModel> Sources { get; } = [];
    [ObservableProperty]
    private string newFieldName = "";

    [ObservableProperty]
    private bool lowercaseValues;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPickerOpen))]
    private ChoiceSourcePickerViewModel? picker;

    public bool IsPickerOpen => Picker is not null;

    partial void OnLowercaseValuesChanged(bool value)
    {
        _parameter.ValueTransform = value ? "lower" : null;
        Changed();
    }

    [RelayCommand]
    private void AddRow()
    {
        Rows.Add(NewRow("", "", false, new Dictionary<string, JsonElement>()));
        SyncRows();
    }

    [RelayCommand]
    private void RemoveRow(ChoiceRowViewModel row)
    {
        Rows.Remove(row);
        SyncRows();
    }

    [RelayCommand]
    private void AddField()
    {
        var name = NewFieldName.Trim();
        if (name.Length == 0 || name is "value" or "label" or "split" || FieldNames.Contains(name))
            return;
        FieldNames.Add(name);
        foreach (var row in Rows)
            row.Fields.Add(new ChoiceFieldCell(name, "", SyncRows));
        NewFieldName = "";
    }

    [RelayCommand]
    private void AddSource() => OpenPicker(null);

    internal void OpenPicker(ChoiceSourceRowViewModel? row)
    {
        _editing = row;
        var picker = new ChoiceSourcePickerViewModel(_environment, row?.Source);
        picker.Closed += OnPickerClosed;
        Picker = picker;
    }

    internal void RemoveSource(ChoiceSourceRowViewModel row)
    {
        Sources.Remove(row);
        SyncSources();
    }

    private void OnPickerClosed(bool accepted)
    {
        var picker = Picker!;
        Picker = null;
        if (!accepted)
            return;
        if (picker.SelectedKind == ChoiceSourceKind.Fixed)
        {
            Rows.Clear();
            foreach (var choice in picker.FixedChoices())
                Rows.Add(NewRow(choice.Label ?? choice.Value, choice.Value, choice.Split, choice.Fields));
            SyncRows();
            return;
        }
        if (picker.BuildSource() is not { } source)
            return;
        var row = new ChoiceSourceRowViewModel(source, this);
        if (_editing is not null && Sources.IndexOf(_editing) is var index and >= 0)
            Sources[index] = row;
        else
            Sources.Add(row);
        SyncSources();
    }

    private ChoiceRowViewModel NewRow(string label, string value, bool split, IReadOnlyDictionary<string, JsonElement> fields) =>
        new(label, value, split, FieldNames.Select(n => new ChoiceFieldCell(n, fields.TryGetValue(n, out var v) ? FieldText(v) : "", SyncRows)),
            SyncRows);

    private static string FieldText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private void SyncRows()
    {
        _parameter.Choices = Rows.Count == 0 ? null : Rows.Select(r => r.ToDefinition()).ToList();
        Changed();
    }

    private void SyncSources()
    {
        _parameter.ChoicesFrom = Sources.Count == 0 ? null : Sources.Select(s => s.Source).ToList();
        Changed();
    }

    private void Changed()
    {
        RefreshPreview();
        _changed();
    }

    private void RefreshPreview() => ShowPreview(_parameter);
}
