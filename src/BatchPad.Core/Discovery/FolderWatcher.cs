namespace BatchPad.Core.Discovery;

/// <param name="OldFullPath">Set for <see cref="WatcherChangeTypes.Renamed"/> only.</param>
public sealed record FileChange(WatcherChangeTypes Kind, string FullPath, string? OldFullPath = null);

/// <summary>
/// Raises <see cref="FileChanged"/> for every file change under the watched folders, and <see cref="FolderChanged"/>
/// once per burst of them. Both are raised on a worker thread.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Timer debounceTimer;
    private readonly TimeSpan debounce;

    public event EventHandler? FolderChanged;

    public event EventHandler<FileChange>? FileChanged;

    /// <param name="directories">Folders that do not exist yet are skipped.</param>
    public FolderWatcher(IEnumerable<string> directories, TimeSpan debounce)
    {
        this.debounce = debounce;
        debounceTimer = new Timer(_ => FolderChanged?.Invoke(this, EventArgs.Empty));
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.Changed += OnChanged;
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        FileChanged?.Invoke(this, new FileChange(e.ChangeType, e.FullPath, (e as RenamedEventArgs)?.OldFullPath));
        debounceTimer.Change(debounce, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        foreach (var watcher in watchers)
            watcher.Dispose();
        debounceTimer.Dispose();
    }
}
