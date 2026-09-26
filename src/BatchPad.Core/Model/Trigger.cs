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

    /// <summary>Every kind field that is set; a valid trigger has exactly one.</summary>
    public IEnumerable<TriggerKind> KindsSet()
    {
        if (Cron is not null)
            yield return TriggerKind.Cron;
        if (Every is not null)
            yield return TriggerKind.Every;
        if (At is not null)
            yield return TriggerKind.At;
        if (FileChanged is not null)
            yield return TriggerKind.FileChanged;
        if (OnStart == true)
            yield return TriggerKind.OnStart;
        if (AfterRun is not null)
            yield return TriggerKind.AfterRun;
    }

    /// <summary>The single kind set, or <see cref="TriggerKind.None"/> when none or several are.</summary>
    [JsonIgnore]
    public TriggerKind Kind => KindsSet().ToList() is [var only] ? only : TriggerKind.None;

    [JsonIgnore]
    public bool IsTimed => Kind is TriggerKind.Cron or TriggerKind.Every or TriggerKind.At;
}
