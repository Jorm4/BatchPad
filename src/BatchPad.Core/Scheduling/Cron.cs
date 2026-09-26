using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BatchPad.Core.Scheduling;

/// <summary>A five-field cron expression (minute hour day month weekday) with lists, ranges, steps and names.</summary>
public sealed class Cron
{
    private const int SearchDays = 366 * 8;

    private static readonly string[] MonthNames = ["", "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] WeekdayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private readonly ulong _minutes;
    private readonly ulong _hours;
    private readonly ulong _days;
    private readonly ulong _months;
    private readonly ulong _weekdays;

    // Classic cron: when both day fields are restricted, a date matching either one is enough.
    private readonly bool _eitherDay;

    private Cron(ulong minutes, ulong hours, ulong days, ulong months, ulong weekdays, bool eitherDay) =>
        (_minutes, _hours, _days, _months, _weekdays, _eitherDay) = (minutes, hours, days, months, weekdays, eitherDay);

    /// <exception cref="FormatException">The expression is not valid.</exception>
    public static Cron Parse(string expression) =>
        TryParse(expression, out var cron, out var error) ? cron : throw new FormatException(error);

    public static bool TryParse(string expression, [NotNullWhen(true)] out Cron? cron, [NotNullWhen(false)] out string? error)
    {
        cron = null;
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            error = $"'{expression}' needs five fields (minute hour day month weekday).";
            return false;
        }
        if (!TryField(fields[0], 0, 59, null, out var minutes, out error)
            || !TryField(fields[1], 0, 23, null, out var hours, out error)
            || !TryField(fields[2], 1, 31, null, out var days, out error)
            || !TryField(fields[3], 1, 12, MonthNames, out var months, out error)
            || !TryField(fields[4], 0, 7, WeekdayNames, out var weekdays, out error))
            return false;
        if ((weekdays & (1UL << 7)) != 0)
            weekdays = (weekdays | 1) & ~(1UL << 7);
        cron = new Cron(minutes, hours, days, months, weekdays, !fields[2].StartsWith('*') && !fields[4].StartsWith('*'));
        return true;
    }

    public bool Matches(DateOnly date)
    {
        if (!Has(_months, date.Month))
            return false;
        var day = Has(_days, date.Day);
        var weekday = Has(_weekdays, (int)date.DayOfWeek);
        return _eitherDay ? day || weekday : day && weekday;
    }

    /// <summary>The first matching wall-clock minute in <paramref name="zone"/> strictly after <paramref name="after"/>.</summary>
    /// <remarks>
    /// The hour repeated when the clocks go back fires only on its first pass, so a cron that fires within that hour
    /// skips the second pass.
    /// </remarks>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        var afterWall = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var earliest = afterWall.AddHours(-3);
        var first = DateOnly.FromDateTime(earliest);
        for (var i = 0; i < SearchDays; i++)
        {
            var date = first.AddDays(i);
            if (!Matches(date))
                continue;
            for (var hour = 0; hour < 24; hour++)
            {
                if (!Has(_hours, hour))
                    continue;
                for (var minute = 0; minute < 60; minute++)
                {
                    if (!Has(_minutes, minute))
                        continue;
                    var wall = date.ToDateTime(new TimeOnly(hour, minute));
                    if (wall < earliest)
                        continue;
                    var instant = TriggerMath.ToInstant(wall, zone);
                    if (instant > after)
                        return instant;
                }
            }
        }
        return null;
    }

    private static bool Has(ulong bits, int value) => (bits & (1UL << value)) != 0;

    private static bool TryField(string field, int min, int max, string[]? names, out ulong bits, [NotNullWhen(false)] out string? error)
    {
        bits = 0;
        error = null;
        foreach (var item in field.Split(','))
        {
            var (range, stepText) = item.Split('/') switch
            {
                [var r] => (r, null),
                [var r, var s] => (r, s),
                _ => (null, null),
            };
            var step = 1;
            if (range is null || stepText is not null && (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1))
            {
                error = $"'{item}' is not a valid cron item.";
                return false;
            }

            int low, high;
            if (range == "*")
                (low, high) = (min, max);
            else if (range.Split('-') is [var from, var to] && TryValue(from, min, max, names, out low) && TryValue(to, min, max, names, out high))
            {
                if (low > high)
                {
                    error = $"'{item}' is a backwards range.";
                    return false;
                }
            }
            else if (TryValue(range, min, max, names, out low))
                high = stepText is null ? low : max;
            else
            {
                error = $"'{item}' is not a value from {min} to {max}.";
                return false;
            }

            for (var value = low; value <= high; value += step)
                bits |= 1UL << value;
        }
        return true;
    }

    private static bool TryValue(string text, int min, int max, string[]? names, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return value >= min && value <= max;
        value = names is null ? -1 : Array.FindIndex(names, n => n.Length > 0 && n.Equals(text, StringComparison.OrdinalIgnoreCase));
        return value >= min;
    }
}
