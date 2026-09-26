using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;

namespace BatchPad.Core.Detection;

/// <summary>
/// Parameter detection that needs an interpreter: Python <c>argparse</c> and PowerShell <c>param()</c> (§3.10).
/// Only for scripts in trusted folders; results are cached by file timestamp.
/// </summary>
public sealed class ScriptProbes(TrustStore trust, InterpreterLocator interpreters, TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, (DateTime Stamp, List<ParameterDefinition> Found)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _started;

    internal int ProcessesStarted => _started;

    /// <summary>Null when the file type has no probe, or with <paramref name="cachedOnly"/> when nothing current is cached.</summary>
    public List<ParameterDefinition>? Parameters(string path, bool cachedOnly = false)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".py" or ".pyw" or ".ps1"))
            return null;
        var fullPath = Path.GetFullPath(path);
        if (!trust.IsTrusted(Path.GetDirectoryName(fullPath)!))
            return [];
        var stamp = File.GetLastWriteTimeUtc(fullPath);
        if (_cache.TryGetValue(fullPath, out var cached) && cached.Stamp == stamp)
            return ConfigJson.Clone(cached.Found);
        if (cachedOnly)
            return null;

        List<ParameterDefinition> found;
        try
        {
            Interlocked.Increment(ref _started);
            found = extension == ".ps1"
                ? PowerShellParamDetector.Detect(interpreters.PowerShell(), fullPath, _timeout)
                : interpreters.Python() is { } python ? ArgparseDetector.Detect(python, fullPath, _timeout) : [];
        }
        catch (Exception ex) when (ex is TimeoutException or RunException or JsonException)
        {
            found = [];
        }
        _cache[fullPath] = (stamp, found);
        return ConfigJson.Clone(found);
    }

    internal static string Text(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        null or JsonValueKind.Null => "",
        _ => node.ToJsonString(),
    };

    internal static JsonNode DefaultFor(ParameterDefinition parameter, JsonNode value) =>
        parameter.Type == ParameterType.Int && value.GetValueKind() == JsonValueKind.Number
            ? JsonValue.Create(value.GetValue<long>())
            : JsonValue.Create(Text(value));
}
