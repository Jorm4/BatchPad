namespace BatchPad.Core.Discovery;

/// <param name="OldFullPath">Set for <see cref="WatcherChangeTypes.Renamed"/> only.</param>
public sealed record FileChange(WatcherChangeTypes Kind, string FullPath, string? OldFullPath = null);

/// <summary>
/// Raises <see cref="FileChanged"/> for every file change under the watched folders, and <see cref="FolderChanged"/>
/// once per burst of them, or after the watcher lost track of changes. Both are raised on a worker thread.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Timer debounceTimer;
    private readonly TimeSpan debounce;
    private readonly Func<string, string, bool>? ignore;
    private volatile bool disposed;

    public event EventHandler? FolderChanged;

    public event EventHandler<FileChange>? FileChanged;

    /// <param name="directories">Folders that do not exist yet are skipped.</param>
    /// <param name="ignore">Given a watched folder and a changed path under it, true to skip the change.</param>
    public FolderWatcher(IEnumerable<string> directories, TimeSpan debounce, Func<string, string, bool>? ignore = null)
    {
        this.debounce = debounce;
        this.ignore = ignore;
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
            watcher.Error += (_, _) => Debounce();
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var oldPath = (e as RenamedEventArgs)?.OldFullPath;
        var root = ((FileSystemWatcher)sender).Path;
        if (ignore is not null && ignore(root, e.FullPath) && (oldPath is null || ignore(root, oldPath)))
            return;
        Notify(new FileChange(e.ChangeType, e.FullPath, oldPath));
    }

    internal void Notify(FileChange change)
    {
        if (disposed)
            return;
        FileChanged?.Invoke(this, change);
        Debounce();
    }

    private void Debounce()
    {
        try
        {
            if (!disposed)
                debounceTimer.Change(debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var watcher in watchers)
            watcher.Dispose();
        debounceTimer.Dispose();
    }
}
