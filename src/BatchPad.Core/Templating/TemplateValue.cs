using System.Text.Json;
using BatchPad.Core.Model;

namespace BatchPad.Core.Templating;

/// <summary>A text or list value (§4.1 typed values). A choice value keeps its definition so <c>.label</c> and extra fields resolve.</summary>
public sealed class TemplateValue
{
    private TemplateValue(string text, IReadOnlyList<string>? items, ChoiceDefinition? choice)
    {
        Text = text;
        Items = items;
        Choice = choice;
    }

    public static TemplateValue Empty { get; } = Of("");

    /// <summary>The value as one string; a list is joined with spaces.</summary>
    public string Text { get; }

    public IReadOnlyList<string>? Items { get; }

    public ChoiceDefinition? Choice { get; }

    public bool IsList => Items is not null;

    public static TemplateValue Of(string text) => new(text, null, null);

    public static TemplateValue OfList(IEnumerable<string> items)
    {
        var list = items.ToList();
        return new(string.Join(' ', list), list, null);
    }

    public static TemplateValue OfChoice(ChoiceDefinition choice) => new(choice.Value, null, choice);

    /// <summary>The items of a list, or a non-empty text as a single item.</summary>
    public IReadOnlyList<string> AsList() => Items ?? (Text.Length == 0 ? [] : [Text]);

    public string? Field(string name)
    {
        if (Choice is null)
            return null;
        if (name == "value")
            return Choice.Value;
        if (name == "label")
            return Choice.DisplayLabel;
        if (!Choice.Fields.TryGetValue(name, out var field))
            return null;
        return field.ValueKind == JsonValueKind.String ? field.GetString() : field.GetRawText();
    }

    internal TemplateValue Map(Func<string, string> transform) =>
        IsList ? OfList(Items!.Select(transform)) : Of(transform(Text));

    public override string ToString() => Text;
}
