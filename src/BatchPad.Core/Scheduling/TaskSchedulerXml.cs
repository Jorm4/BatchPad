using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using BatchPad.Core.Running;

namespace BatchPad.Core.Scheduling;

/// <summary>
/// The Task Scheduler definition of a schedule that runs in Windows: one time trigger at its next fire, whose run
/// registers the fire after it (<c>run --schedule</c>), so BatchPad's own time rules decide every fire.
/// </summary>
public static class TaskSchedulerXml
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string For(TaskHost host, ScheduleEntry entry, string workspaceFile, DateTimeOffset next, TimeZoneInfo zone) =>
        For(host, entry, workspaceFile, Arguments(host, entry, workspaceFile, next, zone), next, zone);

    public static string Arguments(TaskHost host, ScheduleEntry entry, string workspaceFile, DateTimeOffset next, TimeZoneInfo zone) =>
        ArgvQuoter.Join(
        [
            .. host.DataDirectory is { } dataDirectory ? ["--data-dir", dataDirectory] : Array.Empty<string>(),
            "run", "--schedule", entry.Key, "--workspace", workspaceFile, "--due", Local(next, zone),
        ]);

    internal static string For(TaskHost host, ScheduleEntry entry, string workspaceFile, string arguments, DateTimeOffset next, TimeZoneInfo zone)
    {
        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Author", "BatchPad"),
                new XElement(Ns + "Description",
                    $"Runs the BatchPad schedule '{entry.Schedule.Key}' for {entry.Target?.Name ?? entry.Schedule.Target} in {workspaceFile}. "
                    + "Managed by BatchPad: change it on the Schedules page.")),
            new XElement(Ns + "Triggers",
                new XElement(Ns + "TimeTrigger",
                    new XElement(Ns + "StartBoundary", Local(next, zone)))),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings",
                // Every fire must start a process to register the next one; run --schedule applies the overlap policy itself.
                new XElement(Ns + "MultipleInstancesPolicy", "Parallel"),
                new XElement(Ns + "DisallowStartIfOnBatteries", false),
                new XElement(Ns + "StopIfGoingOnBatteries", false),
                // Always, even for missed: skip, so a missed fire still registers the next one; the run then skips itself.
                new XElement(Ns + "StartWhenAvailable", true),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", host.Command),
                    new XElement(Ns + "Arguments", arguments),
                    new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(workspaceFile)))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" + task;
    }

    /// <summary>The arguments of a task definition's first action; null when there are none or it can't be read.</summary>
    public static string? ArgumentsOf(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Descendants(Ns + "Arguments").FirstOrDefault()?.Value;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The workspace file a task's <paramref name="arguments"/> run when that is another file than
    /// <paramref name="workspaceFile"/>; null when it's this one or the arguments don't name one.
    /// </summary>
    public static string? OtherWorkspace(string? arguments, string workspaceFile)
    {
        var argv = ArgvQuoter.Split(arguments ?? "");
        var index = argv.IndexOf("--workspace");
        if (index < 0 || index + 1 >= argv.Count)
            return null;
        var registered = argv[index + 1];
        return SameFile(registered, workspaceFile) ? null : registered;
    }

    // schtasks prints in the console code page, so a non-ASCII character may come back as some other character.
    private static bool SameFile(string registered, string file) =>
        string.Equals(registered, file, StringComparison.OrdinalIgnoreCase)
        || registered.Length == file.Length && registered.Zip(file).All(pair =>
            char.ToUpperInvariant(pair.First) == char.ToUpperInvariant(pair.Second) || !char.IsAscii(pair.Second) && (!char.IsAscii(pair.First) || pair.First == '?'));

    private static string Local(DateTimeOffset time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
