using System.Security.Cryptography;
using System.Text;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Scheduling;

/// <summary>Writes and removes tasks in the user's Windows Task Scheduler.</summary>
public interface ITaskRegistrar
{
    /// <summary>Creates the task, or replaces one of the same name.</summary>
    /// <exception cref="TaskRegistrarException" />
    void Register(string taskName, string xml);

    /// <exception cref="TaskRegistrarException" />
    void Delete(string taskName);

    /// <summary>The tasks directly in <paramref name="folder"/>; none when it doesn't exist.</summary>
    IReadOnlyList<RegisteredTask> Tasks(string folder);
}

/// <param name="Arguments">The command-line arguments of its action, when they could be read.</param>
public sealed record RegisteredTask(string Name, string? Arguments);

public sealed class TaskRegistrarException(string message) : Exception(message);

/// <summary>How a task starts BatchPad, and the Task Scheduler folder its tasks live in.</summary>
/// <param name="DataDirectory">Passed to the task when it isn't the one BatchPad would pick by itself.</param>
public sealed record TaskHost(string Command, string Folder, string? DataDirectory = null)
{
    public const string DefaultFolder = @"\BatchPad\";

    /// <summary>
    /// Runs <paramref name="command"/>. A data folder chosen with <c>BATCHPAD_DATA_DIR</c> gets its own task folder,
    /// so a test or development instance never replaces or removes the tasks of the everyday one.
    /// </summary>
    public static TaskHost For(AppPaths paths, string command)
    {
        var dataDirectory = Path.GetFullPath(paths.DataDirectory);
        if (string.Equals(dataDirectory, Path.GetFullPath(AppPaths.ForCurrentProcess().DataDirectory), StringComparison.OrdinalIgnoreCase))
            return new TaskHost(command, DefaultFolder);
        return new TaskHost(command, $@"{DefaultFolder}data-{ShortHash(dataDirectory.ToUpperInvariant())}\", dataDirectory);
    }

    public static TaskHost ForThisApp(AppPaths paths) => For(paths, Path.Combine(AppContext.BaseDirectory, "BatchPad.exe"));

    public string FolderOf(string workspaceId) => $@"{Folder}{Safe(workspaceId)}\";

    // Task names are case-insensitive and Safe() is lossy, so the hash keeps distinct entries apart.
    public string TaskName(string workspaceId, ScheduleEntry entry) =>
        FolderOf(workspaceId) + (entry.IsGlobal ? "global-" : "") + Safe(entry.Schedule.Key) + "-" + ShortHash(entry.Key);

    // schtasks prints names in the console code page, so only ASCII survives a round trip.
    private static string Safe(string name) =>
        string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private static string ShortHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
}
