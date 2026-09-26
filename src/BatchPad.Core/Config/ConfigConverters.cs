using System.Text.Json;
using System.Text.Json.Serialization;
using BatchPad.Core.Model;

namespace BatchPad.Core.Config;

/// <summary>Picks the node type by which property is present (§3.1): items, steps, url, else script.</summary>
public sealed class TreeNodeConverter : JsonConverter<TreeNode>
{
    public override TreeNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException($"A tree node must be an object, not {element.ValueKind}.");

        var nodeType =
            element.TryGetProperty("items", out _) ? typeof(FolderNode) :
            element.TryGetProperty("steps", out _) ? typeof(WorkflowNode) :
            element.TryGetProperty("url", out _) ? typeof(LinkNode) :
            typeof(ScriptNode);
        return (TreeNode)element.Deserialize(nodeType, options)!;
    }

    public override void Write(Utf8JsonWriter writer, TreeNode value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
}

public sealed class ChoiceDefinitionConverter : JsonConverter<ChoiceDefinition>
{
    public override ChoiceDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Object)
            return new ChoiceDefinition { Value = ScalarText(element) };

        var choice = new ChoiceDefinition();
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "value": choice.Value = ScalarText(property.Value); break;
                case "label": choice.Label = property.Value.GetString(); break;
                case "split": choice.Split = property.Value.GetBoolean(); break;
                default: choice.Fields[property.Name] = property.Value.Clone(); break;
            }
        }
        return choice;
    }

    public override void Write(Utf8JsonWriter writer, ChoiceDefinition value, JsonSerializerOptions options)
    {
        if (value.IsPlain)
        {
            writer.WriteStringValue(value.Value);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("value", value.Value);
        if (value.Label is not null)
            writer.WriteString("label", value.Label);
        if (value.Split)
            writer.WriteBoolean("split", true);
        foreach (var (name, field) in value.Fields)
        {
            writer.WritePropertyName(name);
            field.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Null => "",
        _ => element.GetRawText(),
    };
}

/// <summary><c>choicesFrom</c> is one source object or an array of them.</summary>
public sealed class ChoiceSourceListConverter : JsonConverter<List<ChoiceSource>>
{
    public override List<ChoiceSource>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
            return JsonSerializer.Deserialize<List<ChoiceSource>>(ref reader, options);
        var source = JsonSerializer.Deserialize<ChoiceSource>(ref reader, options);
        return source is null ? null : [source];
    }

    public override void Write(Utf8JsonWriter writer, List<ChoiceSource> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
            JsonSerializer.Serialize(writer, value[0], options);
        else
            JsonSerializer.Serialize(writer, value, options);
    }
}

/// <summary><c>testReport</c> is a path string or an object; a report with only a path is written back as a string.</summary>
public sealed class TestReportConverter : JsonConverter<TestReportDefinition>
{
    public override TestReportDefinition? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? new TestReportDefinition { Path = reader.GetString() }
            : JsonSerializer.Deserialize<TestReportDefinition>(ref reader, options);

    public override void Write(Utf8JsonWriter writer, TestReportDefinition value, JsonSerializerOptions options)
    {
        if (value.IsPlain)
            writer.WriteStringValue(value.Path);
        else
            JsonSerializer.Serialize(writer, value, options);
    }
}

/// <summary>A step's <c>parallel</c> is an array of member steps (a group) or a number (the <c>forEach</c> fan-out).</summary>
internal sealed class StepParallelConverter : JsonConverter<StepParallel>
{
    public override StepParallel? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.StartArray => new StepParallel(JsonSerializer.Deserialize<List<WorkflowStep>>(ref reader, options), null),
            JsonTokenType.Number => new StepParallel(null, reader.GetInt32()),
            _ => throw new JsonException("'parallel' must be an array of steps or a number."),
        };

    public override void Write(Utf8JsonWriter writer, StepParallel value, JsonSerializerOptions options)
    {
        if (value.Members is { } members)
            JsonSerializer.Serialize(writer, members, options);
        else
            writer.WriteNumberValue(value.Degree ?? 1);
    }
}
