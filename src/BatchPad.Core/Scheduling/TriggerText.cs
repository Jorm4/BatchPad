using System.Globalization;
using BatchPad.Core.Model;

namespace BatchPad.Core.Scheduling;

/// <summary>A trigger in words for the Schedules view, e.g. "Weekdays at 02:00".</summary>
public static class TriggerText
{
    private static readonly string[] DayNames = ["Sundays", "Mondays", "Tuesdays", "Wednesdays", "Thursdays", "Fridays", "Saturdays"];

    public static string Describe(Trigger trigger) => trigger.Kind switch
    {
        TriggerKind.Cron => DescribeCron(trigger.Cron!),
        TriggerKind.Every => "Every " + Duration(trigger.Every!) + (trigger.Between is { } between ? $", {between.Replace("-", "–")}" : ""),
        TriggerKind.At => "Once at " + (TriggerMath.ParseAt(trigger.At!, TimeZoneInfo.Local) is { } at
            ? TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : trigger.At),
        TriggerKind.FileChanged => $"When {trigger.FileChanged} changes"
            + (trigger.Debounce is { } debounce ? $" (after {Duration(debounce)} quiet)" : ""),
        TriggerKind.OnStart => "When the workspace opens",
        TriggerKind.AfterRun => $"After {trigger.AfterRun} " + trigger.Result switch
        {
            AfterRunResult.Success => "succeeds",
            AfterRunResult.Failure => "fails",
            _ => "finishes",
        },
        _ => "No trigger",
    };

    /// <summary><c>1h30m</c> → "1 hour 30 minutes"; text that isn't a duration is returned as is.</summary>
    public static string Duration(string text)
    {
        if (TriggerMath.ParseDuration(text) is not { } span)
            return text;
        var parts = new List<string>();
        Add(span.Days, "day");
        Add(span.Hours, "hour");
        Add(span.Minutes, "minute");
        Add(span.Seconds, "second");
        return string.Join(' ', parts);

        void Add(int amount, string unit)
        {
            if (amount > 0)
                parts.Add(amount == 1 ? $"1 {unit}" : $"{amount} {unit}s");
        }
    }

    private static string DescribeCron(string expression)
    {
        if (expression.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not [var minute, var hour, var day, var month, var weekday]
            || !Cron.TryParse(expression, out _, out _))
            return $"cron {expression}";
        if (minute.StartsWith("*/", StringComparison.Ordinal) && hour == "*" && day == "*" && month == "*" && weekday == "*")
            return $"Every {minute[2..]} minutes";
        if (IsNumber(minute) && hour == "*" && day == "*" && month == "*" && weekday == "*")
            return $"Hourly at :{int.Parse(minute, CultureInfo.InvariantCulture):00}";
        if (!IsNumber(minute) || !IsNumber(hour) || month != "*")
            return $"cron {expression}";

        var time = $"{int.Parse(hour, CultureInfo.InvariantCulture):00}:{int.Parse(minute, CultureInfo.InvariantCulture):00}";
        if (day == "*")
            return weekday switch
            {
                "*" => $"Daily at {time}",
                "1-5" or "mon-fri" => $"Weekdays at {time}",
                "0,6" or "6,0" or "sat,sun" or "sun,sat" => $"Weekends at {time}",
                _ when IsNumber(weekday) && int.Parse(weekday, CultureInfo.InvariantCulture) is var d and <= 7 => $"{DayNames[d % 7]} at {time}",
                _ => $"cron {expression}",
            };
        return IsNumber(day) && weekday == "*" ? $"Monthly on day {day} at {time}" : $"cron {expression}";
    }

    private static bool IsNumber(string field) => field.Length > 0 && field.All(char.IsAsciiDigit);
}
