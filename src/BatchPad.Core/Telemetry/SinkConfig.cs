using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BatchPad.Core.Model;

namespace BatchPad.Core.Telemetry;

public static class SinkTypes
{
    public const string Jsonl = "jsonl";
    public const string Otlp = "otlp";
    public const string Elastic = "elastic";
    public const string Influx = "influx";
    public const string Http = "http";

    public static IReadOnlyList<string> All { get; } = [Jsonl, Otlp, Elastic, Influx, Http];
}

/// <summary>
/// One forwarding target. Credential fields hold <c>${env:NAME}</c> references, expanded by <see cref="Resolve"/> at send
/// time only, so settings.json never gets the secret itself.
/// </summary>
public sealed partial class SinkConfig : ExtensibleObject
{
    public const int DefaultMaxSizeMb = 50;

    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public string? Path { get; set; }
    public int? MaxSizeMb { get; set; }

    public string? Endpoint { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string>? Headers { get; set; }

    public string? Index { get; set; }
    public string? ApiKey { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    public string? Org { get; set; }
    public string? Bucket { get; set; }
    public string? Token { get; set; }

    public string Target => (Type == SinkTypes.Jsonl ? Path : Endpoint ?? Url) ?? "";

    /// <summary>Names this sink's outbox queue: its type and destination, so editing credentials keeps what is pending.</summary>
    public string Key
    {
        get
        {
            var identity = string.Join("\n", Type, Path, Endpoint, Url, Index, Org, Bucket).ToLowerInvariant();
            return $"{Type}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12]}";
        }
    }

    /// <summary>A copy with <c>${env:NAME}</c> references (and <c>%NAME%</c> in a path) expanded.</summary>
    /// <exception cref="TelemetrySendException">A referenced variable is not set.</exception>
    public SinkConfig Resolve(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return new SinkConfig
        {
            Type = Type,
            Enabled = Enabled,
            Path = Path is null ? null : Environment.ExpandEnvironmentVariables(Expand(Path, environment)),
            MaxSizeMb = MaxSizeMb,
            Endpoint = Expand(Endpoint, environment),
            Url = Expand(Url, environment),
            Headers = Headers?.ToDictionary(h => h.Key, h => Expand(h.Value, environment)),
            Index = Expand(Index, environment),
            ApiKey = Expand(ApiKey, environment),
            Username = Expand(Username, environment),
            Password = Expand(Password, environment),
            Org = Expand(Org, environment),
            Bucket = Expand(Bucket, environment),
            Token = Expand(Token, environment),
        };
    }

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    public static string? Expand(string? value, Func<string, string?> environment) =>
        value is null ? null : EnvReference().Replace(value, m => environment(m.Groups[1].Value)
            ?? throw new TelemetrySendException($"Environment variable {m.Groups[1].Value} is not set."));

    [GeneratedRegex(@"\$\{env:([^}]+)\}")]
    private static partial Regex EnvReference();
}
