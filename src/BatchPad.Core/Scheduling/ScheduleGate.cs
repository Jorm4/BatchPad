using BatchPad.Core.Model;

namespace BatchPad.Core.Scheduling;

/// <param name="Paused">The target changed since its definition was confirmed, so the schedule must not run it.</param>
/// <param name="Confirmed">The current definition was confirmed, so a run may answer the target's <c>confirm</c>.</param>
/// <param name="State">The schedule's state as checked, created when it had none.</param>
public readonly record struct ScheduleVerdict(bool Paused, bool Confirmed, ScheduleState State);

/// <summary>The definition-hash rules (§4.2) every host that runs a schedule applies, in the app and from the command line.</summary>
public static class ScheduleGate
{
    public const string ChangedMessage = "definition changed — review it in BatchPad";

    // Adopted instead of the current definition when the state file was unreadable, so the schedule waits for a confirm.
    private const string NothingAdopted = "";

    /// <summary>
    /// Checks the target's current <paramref name="hash"/> against the schedule's <c>definitionHash</c>, else the one
    /// adopted when it was first seen (adopting <paramref name="hash"/> now if there is none).
    /// </summary>
    /// <param name="confirmedHere">A hash the user confirmed in this session that isn't saved to the schedule yet.</param>
    public static ScheduleVerdict Check(ScheduleEntry entry, string hash, ScheduleStateStore states, DateTimeOffset now, string? confirmedHere = null)
    {
        var confirmedHash = entry.Schedule.DefinitionHash;
        var state = states.Get(entry.Key);
        if (confirmedHash is null && state?.AdoptedHash is null)
            states.Set(entry.Key, state = (state ?? new ScheduleState { Seen = now }) with { AdoptedHash = states.LoadFailed ? NothingAdopted : hash });
        var expected = confirmedHash ?? state?.AdoptedHash;
        var isConfirmedHere = confirmedHere == hash;
        return new ScheduleVerdict(Paused: expected != hash && !isConfirmedHere, Confirmed: IsConfirmed(entry.Schedule, hash) || isConfirmedHere,
            State: state ?? new ScheduleState { Seen = now });
    }

    /// <summary>The user confirmed this definition and saved it to the schedule, as opposed to it only being adopted.</summary>
    public static bool IsConfirmed(Schedule schedule, string hash) => schedule.DefinitionHash == hash;
}
