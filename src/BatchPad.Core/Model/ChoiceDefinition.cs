using System.Text.Json;
using System.Text.Json.Serialization;
using BatchPad.Core.Config;

namespace BatchPad.Core.Model;

/// <summary>A plain string choice, or a rich choice whose extra fields (e.g. <c>dir</c>) live in <see cref="Fields"/>.</summary>
[JsonConverter(typeof(ChoiceDefinitionConverter))]
public sealed class ChoiceDefinition
{
    public string Value { get; set; } = "";
    public string? Label { get; set; }
    public bool Split { get; set; }
    public Dictionary<string, JsonElement> Fields { get; set; } = [];

    public bool IsPlain => Label is null && !Split && Fields.Count == 0;

    public string DisplayLabel => Label ?? Value;
}

public sealed class ChoiceSource : ExtensibleObject
{
    public string? Glob { get; set; }
    public bool Stem { get; set; }
    public string? Match { get; set; }
    public string? File { get; set; }
    public string? Regex { get; set; }
    public string? Split { get; set; }
    public bool All { get; set; }
    public string? Command { get; set; }
    public string? List { get; set; }
    public string? ValueTransform { get; set; }
    public string? RelativeTo { get; set; }
}
