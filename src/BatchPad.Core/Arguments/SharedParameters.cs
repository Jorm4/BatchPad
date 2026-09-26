using System.Reflection;
using BatchPad.Core.Model;

namespace BatchPad.Core.Arguments;

/// <summary>Resolves <c>{ "use": "app", … }</c> against a workspace's <c>sharedParams</c> (§3.3).</summary>
public static class SharedParameters
{
    private static readonly PropertyInfo[] Properties = typeof(ParameterDefinition)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .ToArray();

    /// <summary>The shared definition with every field the referencing parameter sets laid over it.</summary>
    public static ParameterDefinition Merge(ParameterDefinition parameter, IReadOnlyDictionary<string, ParameterDefinition>? shared)
    {
        if (parameter.Use is null)
            return parameter;
        if (shared?.GetValueOrDefault(parameter.Use) is not { } definition)
            throw new ArgumentAssemblyException($"Parameter uses \"{parameter.Use}\", which is not in sharedParams.");

        var merged = new ParameterDefinition();
        foreach (var property in Properties)
        {
            var value = property.GetValue(parameter) ?? property.GetValue(definition);
            property.SetValue(merged, value);
        }
        merged.Name ??= parameter.Use;
        merged.Use = null;
        return merged;
    }

    public static IReadOnlyList<ParameterDefinition> MergeAll(IEnumerable<ParameterDefinition>? parameters, IReadOnlyDictionary<string, ParameterDefinition>? shared) =>
        (parameters ?? []).Select(p => Merge(p, shared)).ToList();
}
