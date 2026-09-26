using System.Text.Json.Nodes;
using BatchPad.Core.Choices;
using BatchPad.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Parameters;

public sealed record ChoiceOption(ChoiceDefinition Choice)
{
    public string Label => Choice.DisplayLabel.Length > 0 ? Choice.DisplayLabel : "(none)";
    public string Value => Choice.Value;
    public override string ToString() => Label;
}

/// <summary>A field whose choices resolve off the UI thread: it shows "Loading…" until <see cref="Fill"/>.</summary>
public abstract partial class ResolvedFieldViewModel(ParameterDefinition definition, JsonNode? stored, bool isSet)
    : ParameterFieldViewModel(definition, isSet)
{
    protected JsonNode? Stored { get; } = stored;
    protected JsonNode? Initial => Stored ?? Definition.Default;

    public bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    private string? problems = "Loading…";

    public void Fill(ResolvedChoices choices)
    {
        Show(choices);
        Problems = choices.Problems.Count == 0 ? null : string.Join(Environment.NewLine, choices.Problems);
        IsLoading = false;
        OnPropertyChanged(nameof(IsLoading));
        EndLoad();
    }

    protected abstract void Show(ResolvedChoices choices);
}

public sealed partial class ChoiceFieldViewModel(ParameterDefinition definition, JsonNode? stored, bool isSet)
    : ResolvedFieldViewModel(definition, stored, isSet)
{
    public const int MaxSegments = 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSegmented))]
    private IReadOnlyList<ChoiceOption> options = [];

    public bool IsSegmented => Options.Count <= MaxSegments;

    [ObservableProperty]
    private ChoiceOption? selected;

    public override JsonNode? Value => IsLoading ? Stored?.DeepClone() : Selected is null ? null : JsonValue.Create(Selected.Value);

    protected override void Show(ResolvedChoices choices)
    {
        Options = choices.Choices.Select(c => new ChoiceOption(c)).ToList();
        if (TextOf(Initial) is { } initial)
        {
            var value = ValueNormalizer.Normalize(initial, choices.Choices).Value;
            Selected = Options.FirstOrDefault(o => o.Value == value);
        }
    }

    partial void OnSelectedChanged(ChoiceOption? value) => OnEdited();
}

public sealed partial class MultichoiceItem(ChoiceOption option, bool isChecked, Action onChecked) : ObservableObject
{
    public ChoiceOption Option { get; } = option;
    public string Label => Option.Label;

    [ObservableProperty]
    private bool isChecked = isChecked;

    [ObservableProperty]
    private bool isVisible = true;

    partial void OnIsCheckedChanged(bool value) => onChecked();
}

public sealed partial class MultichoiceFieldViewModel(ParameterDefinition definition, JsonNode? stored, bool isSet)
    : ResolvedFieldViewModel(definition, stored, isSet)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private IReadOnlyList<MultichoiceItem> items = [];

    protected override void Show(ResolvedChoices choices)
    {
        var selected = Initial switch
        {
            JsonArray array => array.Select(TextOf).OfType<string>(),
            null => [],
            var single => (TextOf(single) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
        };
        var values = ValueNormalizer.Normalize(selected, choices.Choices).Select(v => v.Value).ToHashSet();
        Items = choices.Choices.Select(c => new MultichoiceItem(new ChoiceOption(c), values.Contains(c.Value), OnItemChecked)).ToList();
    }

    public bool EmptyMeansAll => Definition.EmptyMeans == "all";
    public string ClearLabel => EmptyMeansAll ? "Clear = all" : "Clear";

    public string Summary
    {
        get
        {
            var picked = Items.Where(i => i.IsChecked).Select(i => i.Label).ToList();
            return picked.Count switch
            {
                0 => EmptyMeansAll ? "All" : "None",
                <= 3 => string.Join(", ", picked),
                _ => $"{picked.Count} of {Items.Count}",
            };
        }
    }

    [ObservableProperty]
    private string filter = "";

    public override JsonNode? Value => IsLoading
        ? Stored?.DeepClone()
        : new JsonArray([.. Items.Where(i => i.IsChecked).Select(i => (JsonNode)JsonValue.Create(i.Option.Value))]);

    partial void OnFilterChanged(string value)
    {
        foreach (var item in Items)
            item.IsVisible = item.Label.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (var item in Items)
            item.IsChecked = false;
    }

    private void OnItemChecked()
    {
        OnPropertyChanged(nameof(Summary));
        OnEdited();
    }
}
