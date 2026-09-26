using System.Globalization;
using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using CommunityToolkit.Mvvm.ComponentModel;
using static BatchPad.App.ViewModels.Editor.GeneralTabViewModel;

namespace BatchPad.App.ViewModels.Editor;

/// <summary>Edits one <see cref="ParameterDefinition"/> in place; a <c>use</c> entry edits only its overrides.</summary>
public sealed partial class ParameterEditorViewModel : ObservableObject
{
    private readonly Action _changed;

    public ParameterEditorViewModel(ParameterDefinition definition, Action changed)
    {
        Definition = definition;
        name = definition.Name ?? "";
        label = definition.Label ?? "";
        type = TypeOptions.Single(o => o.Value == definition.Type);
        description = definition.Description ?? "";
        arg = definition.Arg ?? "";
        defaultText = DefaultToText(definition.Default);
        required = definition.Required == true;
        envVar = definition.EnvVar ?? "";
        ask = definition.Ask == true;
        emit = definition.Emit != false;
        split = definition.Split == true;
        maxPerCallText = definition.MaxPerCall?.ToString(CultureInfo.InvariantCulture) ?? "";
        emptyMeansAll = definition.EmptyMeans == "all";
        emptyArgs = definition.EmptyArgs is { } args ? ArgvQuoter.Join(args) : "";
        lowercaseValues = definition.ValueTransform == "lower";
        _changed = changed;
    }

    public ParameterDefinition Definition { get; }

    private static readonly IReadOnlyList<EditorOption<ParameterType?>> TypeOptions =
    [
        new(null, "(from shared)"), new(ParameterType.Flag, "Flag"), new(ParameterType.Choice, "Choice"),
        new(ParameterType.Multichoice, "Multichoice"), new(ParameterType.Text, "Text"), new(ParameterType.Int, "Number"),
        new(ParameterType.Path, "Path"), new(ParameterType.Secret, "Secret"),
    ];

    public IReadOnlyList<EditorOption<ParameterType?>> Types => TypeOptions;
    public string? Use => Definition.Use;
    public bool IsShared => Definition.Use is not null;
    public string DisplayName => NullIfEmpty(Label) ?? NullIfEmpty(Name) ?? Use ?? "(unnamed)";
    public bool HasChoices => Type.Value is ParameterType.Choice or ParameterType.Multichoice;
    public bool ShowsChoiceOptions => HasChoices || IsShared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChoices), nameof(ShowsChoiceOptions))]
    private EditorOption<ParameterType?> type;

    [ObservableProperty]
    private string description;

    [ObservableProperty]
    private string arg;

    [ObservableProperty]
    private string defaultText;

    [ObservableProperty]
    private bool required;

    [ObservableProperty]
    private string envVar;

    [ObservableProperty]
    private bool ask;

    [ObservableProperty]
    private bool emit;

    [ObservableProperty]
    private bool split;

    [ObservableProperty]
    private string maxPerCallText;

    [ObservableProperty]
    private bool emptyMeansAll;

    [ObservableProperty]
    private string emptyArgs;

    [ObservableProperty]
    private bool lowercaseValues;

    partial void OnNameChanged(string value) => Set(() => Definition.Name = NullIfEmpty(value));
    partial void OnLabelChanged(string value) => Set(() => Definition.Label = NullIfEmpty(value));
    partial void OnDescriptionChanged(string value) => Set(() => Definition.Description = NullIfEmpty(value));
    partial void OnArgChanged(string value) => Set(() => Definition.Arg = NullIfEmpty(value));
    partial void OnRequiredChanged(bool value) => Set(() => Definition.Required = value ? true : null);
    partial void OnEnvVarChanged(string value) => Set(() => Definition.EnvVar = NullIfEmpty(value));
    partial void OnAskChanged(bool value) => Set(() => Definition.Ask = value ? true : null);
    partial void OnEmitChanged(bool value) => Set(() => Definition.Emit = value ? null : false);
    partial void OnSplitChanged(bool value) => Set(() => Definition.Split = value ? true : null);
    partial void OnEmptyMeansAllChanged(bool value) => Set(() => Definition.EmptyMeans = value ? "all" : null);
    partial void OnEmptyArgsChanged(string value) => Set(() => Definition.EmptyArgs = SplitOrNull(value));
    partial void OnLowercaseValuesChanged(bool value) => Set(() => Definition.ValueTransform = value ? "lower" : null);

    partial void OnMaxPerCallTextChanged(string value) => Set(() =>
        Definition.MaxPerCall = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) && max > 0 ? max : null);

    internal static List<string>? SplitOrNull(string text) => ArgumentAssembler.SplitArguments(text) is { Count: > 0 } args ? args : null;

    partial void OnTypeChanged(EditorOption<ParameterType?> value) => Set(() =>
    {
        Definition.Type = value.Value;
        Definition.Default = TextToDefault(DefaultText, value.Value);
    });

    partial void OnDefaultTextChanged(string value) => Set(() => Definition.Default = TextToDefault(value, Type.Value));

    private void Set(Action apply)
    {
        apply();
        _changed();
    }

    private static string DefaultToText(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray list => string.Join(" ", list.Select(i => i?.ToString())),
        _ => value.ToJsonString(),
    };

    private static JsonNode? TextToDefault(string text, ParameterType? type)
    {
        if (text.Length == 0)
            return null;
        if (type == ParameterType.Int && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return JsonValue.Create(number);
        if (type == ParameterType.Flag && bool.TryParse(text, out var flag))
            return JsonValue.Create(flag);
        if (type == ParameterType.Multichoice)
            return new JsonArray(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        return JsonValue.Create(text);
    }
}
