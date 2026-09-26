using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BatchPad.Core.Config;

public static class ConfigJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            IndentSize = 2,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new TreeNodeConverter() },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ApplyWriteConventions } },
        };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// Writes base-class properties first (so <c>id</c> and <c>name</c> lead) and unknown properties last, and skips a scalar property whose value
    /// equals the one a freshly constructed object has.
    /// </summary>
    private static void ApplyWriteConventions(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Type.IsAbstract
            || typeInfo.Type.GetConstructor(Type.EmptyTypes) is null)
            return;

        var pristine = Activator.CreateInstance(typeInfo.Type)!;
        foreach (var property in typeInfo.Properties)
        {
            if (property.IsExtensionData)
                property.Order = int.MaxValue;
            else if (property.AttributeProvider is System.Reflection.MemberInfo member)
                property.Order = InheritanceDepth(member.DeclaringType!);

            if (property.Get is null || !(property.PropertyType.IsValueType || property.PropertyType == typeof(string)))
                continue;
            var defaultValue = property.Get(pristine);
            property.ShouldSerialize = (_, value) => !Equals(value, defaultValue);
        }
    }

    private static int InheritanceDepth(Type type)
    {
        var depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            depth++;
        return depth;
    }
}
