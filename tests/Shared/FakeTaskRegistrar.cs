using BatchPad.Core.Scheduling;

namespace BatchPad.Tests;

internal sealed class FakeTaskRegistrar : ITaskRegistrar
{
    public Dictionary<string, string> Registered { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Deleted { get; } = [];
    public string? Error { get; set; }

    public void Register(string taskName, string xml)
    {
        if (Error is { } error)
            throw new TaskRegistrarException(error);
        lock (Registered)
            Registered[taskName] = xml;
    }

    public void Delete(string taskName)
    {
        lock (Registered)
        {
            Deleted.Add(taskName);
            if (!Registered.Remove(taskName))
                throw new TaskRegistrarException($"No task {taskName}.");
        }
    }

    public IReadOnlyList<RegisteredTask> Tasks(string folder)
    {
        lock (Registered)
        {
            return
            [
                .. Registered.Where(t => t.Key.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && !t.Key[folder.Length..].Contains('\\'))
                    .Select(t => new RegisteredTask(t.Key, TaskSchedulerXml.ArgumentsOf(t.Value))),
            ];
        }
    }
}
