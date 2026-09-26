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

public sealed partial class ChoiceFieldViewModel : ParameterFieldViewModel
{
    public const int MaxSegments = 4;

    public ChoiceFieldViewModel(ParameterDefinition definition, ResolvedChoices choices, JsonNode? stored, bool isSet)
        : base(definition, isSet)
    {
        Options = choices.Choices.Select(c => new ChoiceOption(c)).ToList();
        Problems = choices.Problems.Count == 0 ? null : string.Join(Environment.NewLine, choices.Problems);
        if (TextOf(stored ?? definition.Default) is { } initial)
        {
            var value = ValueNormalizer.Normalize(initial, choices.Choices).Value;
            Selected = Options.FirstOrDefault(o => o.Value == value);
        }
        EndLoad();
    }

    public IReadOnlyList<ChoiceOption> Options { get; }
    public bool IsSegmented => Options.Count <= MaxSegments;
    public string? Problems { get; }

    [ObservableProperty]
    private ChoiceOption? selected;

    public override JsonNode? Value => Selected is null ? null : JsonValue.Create(Selected.Value);

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

public sealed partial class MultichoiceFieldViewModel : ParameterFieldViewModel
{
    public MultichoiceFieldViewModel(ParameterDefinition definition, ResolvedChoices choices, JsonNode? stored, bool isSet)
        : base(definition, isSet)
    {
        var initial = stored ?? definition.Default;
        var selected = initial switch
        {
            JsonArray array => array.Select(TextOf).OfType<string>(),
            null => [],
            _ => (TextOf(initial) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
        };
        var values = ValueNormalizer.Normalize(selected, choices.Choices).Select(v => v.Value).ToHashSet();
        Items = choices.Choices.Select(c => new MultichoiceItem(new ChoiceOption(c), values.Contains(c.Value), OnItemChecked)).ToList();
        Problems = choices.Problems.Count == 0 ? null : string.Join(Environment.NewLine, choices.Problems);
        EndLoad();
    }

    public IReadOnlyList<MultichoiceItem> Items { get; }
    public string? Problems { get; }
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

    public override JsonNode? Value => new JsonArray([.. Items.Where(i => i.IsChecked).Select(i => (JsonNode)JsonValue.Create(i.Option.Value))]);

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
