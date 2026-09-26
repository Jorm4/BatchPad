using System.Text.RegularExpressions;
using BatchPad.Core.Model;
using BatchPad.Core.Templating;

namespace BatchPad.Core.Running;

/// <param name="Url">The expanded <c>ready.open</c>, or null when the script sets none.</param>
public sealed record ReadySignal(string? Url, OutputLine? Line);

/// <summary>
/// Watches a long-running run's output for its <c>ready</c> pattern (§3.2). Without a pattern the run is ready once
/// started, as for launched apps. <see cref="Ready"/> gives null when the run ends first.
/// </summary>
public sealed partial class ReadyWatcher : IDisposable
{
    private readonly TaskCompletionSource<ReadySignal?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IDisposable _subscription;

    private ReadyWatcher(IRunOutput run, ReadyDefinition? ready, TemplateContext templates, IShellOpener? opener)
    {
        var pattern = ready?.Pattern is { } text ? new Regex(text) : null;
        _ = _ready.Task.ContinueWith(t =>
        {
            if (t.Result?.Url is { } url)
                opener?.Open(url);
        }, TaskContinuationOptions.OnlyOnRanToCompletion);

        if (pattern is null)
        {
            _ready.TrySetResult(new ReadySignal(Expand(ready?.Open, null, templates), null));
            _subscription = Disposable.None;
            return;
        }
        _subscription = run.Subscribe(line =>
        {
            if (_ready.Task.IsCompleted || line.Stream == OutputStream.Info || pattern.Match(line.Text) is not { Success: true } match)
                return;
            try
            {
                _ready.TrySetResult(new ReadySignal(Expand(ready!.Open, match, templates), line));
            }
            catch (TemplateException ex)
            {
                _ready.TrySetException(ex);
            }
        });
        _ = run.Completion.ContinueWith(_ => _ready.TrySetResult(null), TaskScheduler.Default);
    }

    public Task<ReadySignal?> Ready => _ready.Task;

    /// <param name="opener">Opens the ready URL when given.</param>
    public static ReadyWatcher Watch(IRunOutput run, RunRequest request, IShellOpener? opener = null) =>
        new(run, request.Script.Ready, RunPlanner.BoundTemplatesFor(request), opener);

    public static ReadyWatcher Watch(IRunOutput run, ReadyDefinition? ready, TemplateContext templates, IShellOpener? opener = null) =>
        new(run, ready, templates, opener);

    /// <summary>Replaces <c>$1</c>… with the match's groups, then expands <c>${…}</c> variables.</summary>
    public static string? Expand(string? open, Match? match, TemplateContext templates)
    {
        if (open is null)
            return null;
        var withGroups = GroupReference().Replace(open, m =>
            match is not null && int.Parse(m.Groups[1].Value) is var index && index < match.Groups.Count
                ? match.Groups[index].Value
                : "");
        return TemplateExpander.ExpandText(withGroups, templates);
    }

    public void Dispose() => _subscription.Dispose();

    [GeneratedRegex(@"\$(\d+)")]
    private static partial Regex GroupReference();

    private sealed class Disposable : IDisposable
    {
        public static readonly Disposable None = new();
        public void Dispose() { }
    }
}
