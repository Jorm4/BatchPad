using System.Text.Json.Serialization;

namespace BatchPad.Core.Model;

public enum TriggerKind { None, Cron, Every, At, FileChanged, OnStart, AfterRun }

public enum AfterRunResult { Success, Failure, Always }

/// <summary>When a schedule fires (§4.2): exactly one of the kind fields is set.</summary>
public sealed class Trigger : ExtensibleObject
{
    public string? Cron { get; set; }
    public string? Every { get; set; }

    /// <summary><c>HH:mm-HH:mm</c> window for <see cref="Every"/>; it may wrap past midnight.</summary>
    public string? Between { get; set; }

    public string? At { get; set; }
    public string? FileChanged { get; set; }
    public string? Debounce { get; set; }
    public bool? OnStart { get; set; }
    public string? AfterRun { get; set; }
    public AfterRunResult? Result { get; set; }

    private static readonly (TriggerKind Kind, Func<Trigger, bool> IsSet)[] Kinds =
    [
        (TriggerKind.Cron, t => t.Cron is not null),
        (TriggerKind.Every, t => t.Every is not null),
        (TriggerKind.At, t => t.At is not null),
        (TriggerKind.FileChanged, t => t.FileChanged is not null),
        (TriggerKind.OnStart, t => t.OnStart == true),
        (TriggerKind.AfterRun, t => t.AfterRun is not null),
    ];

    public IEnumerable<TriggerKind> KindsSet() => Kinds.Where(k => k.IsSet(this)).Select(k => k.Kind);

    [JsonIgnore]
    public TriggerKind Kind
    {
        get
        {
            var found = TriggerKind.None;
            foreach (var (kind, isSet) in Kinds)
            {
                if (!isSet(this))
                    continue;
                if (found != TriggerKind.None)
                    return TriggerKind.None;
                found = kind;
            }
            return found;
        }
    }

    [JsonIgnore]
    public bool IsTimed => Kind is TriggerKind.Cron or TriggerKind.Every or TriggerKind.At;
}
