using System.Text.Json;
using System.Xml;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;

namespace BatchPad.Core.Output;

/// <summary>A report file a finished run wrote, at a path relative to its tree.</summary>
internal static class RunReportFile
{
    private static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(2);

    /// <summary>The report read by <paramref name="read"/>; null when it is missing, unreadable or older than the run, so left over from an earlier one.</summary>
    public static T? Read<T>(string? relativePath, RunRequest request, DateTimeOffset startedAt, Func<string, T> read) where T : class
    {
        if (relativePath is not { Length: > 0 })
            return null;
        try
        {
            var path = TemplateExpander.ExpandPath(relativePath, request.Tree.BaseDirectory, RunPlanner.BoundTemplatesFor(request));
            return WrittenSince(path, startedAt) ? read(path) : null;
        }
        catch (Exception ex) when (ex is TemplateException or JsonException or XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool WrittenSince(string path, DateTimeOffset startedAt) =>
        File.Exists(path) && File.GetLastWriteTimeUtc(path) >= startedAt.UtcDateTime - ClockSlack;
}
