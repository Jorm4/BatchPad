namespace BatchPad.Core.Discovery;

/// <summary>Raises <see cref="FolderChanged"/> once per burst of file changes under the watched folders.</summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Timer debounceTimer;
    private readonly TimeSpan debounce;

    public event EventHandler? FolderChanged;

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

    private void OnChanged(object sender, FileSystemEventArgs e) => debounceTimer.Change(debounce, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        foreach (var watcher in watchers)
            watcher.Dispose();
        debounceTimer.Dispose();
    }
}
