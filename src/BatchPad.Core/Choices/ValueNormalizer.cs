using BatchPad.Core.Model;

namespace BatchPad.Core.Choices;

/// <param name="Changed">A label was given and <see cref="Value"/> is its choice's value, to be written back on the next save.</param>
public sealed record NormalizedValue(string Value, bool Changed, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>Stored values are choice values; a label matching exactly one choice is accepted and rewritten (§3.3).</summary>
public static class ValueNormalizer
{
    public static NormalizedValue Normalize(string stored, IReadOnlyList<ChoiceDefinition> choices)
    {
        if (choices.Any(c => c.Value == stored))
            return new(stored, false, null);

        var labelled = choices.Where(c => c.Label == stored).ToList();
        return labelled.Count switch
        {
            0 => new(stored, false, null),
            1 => new(labelled[0].Value, true, null),
            _ => new(stored, false, $"\"{stored}\" is the label of {labelled.Count} choices; store one of their values instead."),
        };
    }

    public static IReadOnlyList<NormalizedValue> Normalize(IEnumerable<string> stored, IReadOnlyList<ChoiceDefinition> choices) =>
        stored.Select(value => Normalize(value, choices)).ToList();
}
