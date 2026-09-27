using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace BatchPad.Core.Scheduling;

/// <summary>Registers tasks through <c>schtasks.exe</c>, which needs no COM interop and no extra package.</summary>
public sealed partial class SchtasksRegistrar : ITaskRegistrar
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public void Register(string taskName, string xml)
    {
        var file = Path.Combine(Path.GetTempPath(), $"batchpad-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            Run("/Create", "/TN", taskName, "/XML", file, "/F");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TaskRegistrarException(ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Delete(string taskName) => Run("/Delete", "/TN", taskName, "/F");

    public IReadOnlyList<RegisteredTask> Tasks(string folder)
    {
        try
        {
            return ParseQuery(Run("/Query", "/TN", folder, "/XML"), folder);
        }
        catch (TaskRegistrarException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads <c>schtasks /Query /XML</c> output: a <c>&lt;Tasks&gt;</c> list where a <c>&lt;!-- name --&gt;</c> comment
    /// precedes each task's definition. Keeps only the tasks directly in <paramref name="folder"/>.
    /// </summary>
    public static IReadOnlyList<RegisteredTask> ParseQuery(string output, string folder) =>
    [
        .. QueryEntry().Matches(output)
            .Select(match => new RegisteredTask(match.Groups["name"].Value.Trim(),
                TaskSchedulerXml.ArgumentsOf(XmlDeclaration().Replace(match.Groups["task"].Value, ""))))
            .Where(task => task.Name.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && !task.Name[folder.Length..].Contains('\\')),
    ];

    [GeneratedRegex(@"<!--(?<name>.*?)-->(?<task>.*?)(?=<!--|</Tasks>|$)", RegexOptions.Singleline)]
    private static partial Regex QueryEntry();

    [GeneratedRegex(@"<\?xml[^>]*\?>")]
    private static partial Regex XmlDeclaration();

    private static string Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Limit))
            {
                process.Kill(entireProcessTree: true);
                throw new TaskRegistrarException("Task Scheduler did not answer.");
            }
            if (process.ExitCode != 0)
                throw new TaskRegistrarException(error.Result.Trim() is { Length: > 0 } message ? message : $"schtasks exited with code {process.ExitCode}.");
            return output.Result;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new TaskRegistrarException(ex.Message);
        }
    }
}
