using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BatchPad.Core.Config;

namespace BatchPad.Core.Model;

public sealed class ParameterDefinition : ExtensibleObject
{
    public string? Name { get; set; }
    public string? Use { get; set; }
    public string? Label { get; set; }
    public ParameterType? Type { get; set; }
    public string? Description { get; set; }
    public bool? Required { get; set; }
    public JsonNode? Default { get; set; }
    public string? Arg { get; set; }
    public List<ChoiceDefinition>? Choices { get; set; }

    [JsonConverter(typeof(ChoiceSourceListConverter))]
    public List<ChoiceSource>? ChoicesFrom { get; set; }

    public string? ValueTransform { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
    public bool? Split { get; set; }
    public string? Mode { get; set; }
    public string? Position { get; set; }
    public bool? Emit { get; set; }
    public string? EnvVar { get; set; }
    public string? EmptyMeans { get; set; }
    public List<string>? EmptyArgs { get; set; }
    public bool? RepeatArg { get; set; }
    public int? MaxPerCall { get; set; }
    public bool? Ask { get; set; }
}

public enum ParameterType { Flag, Choice, Multichoice, Text, Int, Path, Secret }
