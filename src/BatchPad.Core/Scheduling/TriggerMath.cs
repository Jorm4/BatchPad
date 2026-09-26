using System.Globalization;
using System.Text.RegularExpressions;
using BatchPad.Core.Model;

namespace BatchPad.Core.Scheduling;

/// <summary>When time triggers fire (§4.2). Times are wall-clock times in a time zone, so daylight-saving changes are honoured.</summary>
public static partial class TriggerMath
{
    /// <summary>The next fire strictly after <paramref name="after"/>; null for an event trigger, a past <c>at</c>, or an invalid trigger.</summary>
    public static DateTimeOffset? NextFire(Trigger trigger, DateTimeOffset after, TimeZoneInfo zone) => trigger.Kind switch
    {
        TriggerKind.Cron => Cron.TryParse(trigger.Cron!, out var cron, out _) ? cron.Next(after, zone) : null,
        TriggerKind.Every => ParseDuration(trigger.Every!) is { } interval && TryParseWindow(trigger.Between, out var window)
            ? NextEvery(interval, window, after, zone)
            : null,
        TriggerKind.At => ParseAt(trigger.At!, zone) is { } at && at > after ? at : null,
        _ => null,
    };

    /// <summary>Why <paramref name="trigger"/> can't be used, or null when it can.</summary>
    public static string? Problem(Trigger trigger)
    {
        if (trigger.Kind == TriggerKind.None)
            return "A trigger needs exactly one of cron, every, at, fileChanged, onStart and afterRun.";
        if (trigger.Cron is { } cron && !Cron.TryParse(cron, out _, out var cronError))
            return cronError;
        if (trigger.Every is { } every && ParseDuration(every) is null)
            return $"'{every}' is not a duration such as 30m or 1h30m.";
        if (!TryParseWindow(trigger.Between, out _))
            return $"'{trigger.Between}' is not a window such as 09:00-18:00.";
        if (trigger.At is { } at && ParseAt(at, TimeZoneInfo.Utc) is null)
            return $"'{at}' is not a date and time such as 2026-12-24T18:00.";
        if (trigger.Debounce is { } debounce && ParseDuration(debounce) is null)
            return $"'{debounce}' is not a duration such as 5s.";
        return null;
    }

    /// <summary>Parses <c>90s</c>, <c>30m</c>, <c>1h30m</c> or <c>2d</c>; null when it isn't a positive duration.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        var match = DurationPattern().Match(text.Trim());
        if (!match.Success)
            return null;
        var total = TimeSpan.Zero;
        for (var i = 0; i < match.Groups["n"].Captures.Count; i++)
        {
            var amount = int.Parse(match.Groups["n"].Captures[i].Value, CultureInfo.InvariantCulture);
            total += match.Groups["unit"].Captures[i].Value switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromDays(amount),
            };
        }
        return total > TimeSpan.Zero ? total : null;
    }

    /// <summary>True for a missing window (the whole day) or a valid <c>HH:mm-HH:mm</c> one.</summary>
    public static bool TryParseWindow(string? text, out (TimeOnly Start, TimeOnly End)? window)
    {
        window = null;
        if (text is null)
            return true;
        if (text.Split('-') is not [var from, var to]
            || !TimeOnly.TryParseExact(from.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(to.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            return false;
        window = (start, end);
        return true;
    }

    /// <summary>An <c>at</c> time: with an offset or <c>Z</c> it is exact, otherwise it is wall-clock time in <paramref name="zone"/>.</summary>
    public static DateTimeOffset? ParseAt(string text, TimeZoneInfo zone)
    {
        text = text.Trim();
        if (OffsetSuffix().IsMatch(text))
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact) ? exact : null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)
            ? ToInstant(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone)
            : null;
    }

    /// <summary>
    /// The instant a wall-clock time names. A time skipped by a spring-forward change maps to the moment the clocks jump;
    /// a time repeated by a fall-back change maps to its first occurrence.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime wall, TimeZoneInfo zone)
    {
        for (var i = 0; zone.IsInvalidTime(wall) && i < 24 * 60; i++)
            wall = wall.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset);
    }

    private static DateTimeOffset NextEvery(TimeSpan interval, (TimeOnly Start, TimeOnly End)? window, DateTimeOffset after, TimeZoneInfo zone)
    {
        if (window is null && interval > TimeSpan.FromDays(1))
            return new DateTimeOffset((after.UtcTicks / interval.Ticks + 1) * interval.Ticks, TimeSpan.Zero);

        var start = window?.Start.ToTimeSpan() ?? TimeSpan.Zero;
        var length = window is { } w ? w.End.ToTimeSpan() - start : TimeSpan.FromDays(1);
        if (length <= TimeSpan.Zero)
            length += TimeSpan.FromDays(1);

        var afterWall = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        for (var day = DateOnly.FromDateTime(afterWall).AddDays(-1); ; day = day.AddDays(1))
        {
            var open = day.ToDateTime(TimeOnly.MinValue) + start;
            var skip = Math.Max(0, (long)Math.Floor((afterWall - open - TimeSpan.FromHours(3)) / interval));
            for (var k = skip; k * interval <= length; k++)
            {
                var instant = ToInstant(open + k * interval, zone);
                if (instant > after)
                    return instant;
            }
        }
    }

    [GeneratedRegex(@"^(?:(?<n>\d{1,6})(?<unit>[smhd]))+$")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetSuffix();
}
