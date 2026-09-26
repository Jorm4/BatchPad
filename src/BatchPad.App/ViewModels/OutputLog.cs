using System.Collections.ObjectModel;
using BatchPad.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

/// <summary>A run's output lines with search and error navigation (F8 / Shift+F8), for run tabs and workflow steps alike.</summary>
/// <param name="onErrorSelected">Called when navigation selects a line, so the tab can stop following new output.</param>
public sealed partial class OutputLog(Action? onErrorSelected = null) : ObservableObject
{
    private ObservableCollection<OutputLineViewModel>? _matches;

    public ObservableCollection<OutputLineViewModel> Lines { get; } = [];

    /// <summary><see cref="Lines"/>, or only those containing <see cref="SearchText"/> while searching.</summary>
    public IReadOnlyList<OutputLineViewModel> DisplayedLines => _matches ?? (IReadOnlyList<OutputLineViewModel>)Lines;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private OutputLineViewModel? selectedLine;

    partial void OnSearchTextChanged(string value)
    {
        _matches = value.Length == 0 ? null : new(Lines.Where(Matches));
        OnPropertyChanged(nameof(DisplayedLines));
    }

    private bool Matches(OutputLineViewModel line) => line.Text.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    public void Add(OutputLineViewModel line)
    {
        Lines.Add(line);
        if (_matches is not null && Matches(line))
            _matches.Add(line);
    }

    [RelayCommand]
    private void NextError() => MoveToError(1);

    [RelayCommand]
    private void PreviousError() => MoveToError(-1);

    private void MoveToError(int direction)
    {
        var lines = DisplayedLines;
        var count = lines.Count;
        var current = SelectedLine is null ? -1 : IndexOf(lines, SelectedLine);
        if (current < 0)
            current = direction > 0 ? -1 : count;
        for (var step = 1; step <= count; step++)
        {
            var line = lines[((current + direction * step) % count + count) % count];
            if (line.IsErrorMatch || line.IsError)
            {
                onErrorSelected?.Invoke();
                SelectedLine = line;
                return;
            }
        }
    }

    private static int IndexOf(IReadOnlyList<OutputLineViewModel> lines, OutputLineViewModel line)
    {
        for (var i = 0; i < lines.Count; i++)
            if (ReferenceEquals(lines[i], line))
                return i;
        return -1;
    }
}

/// <summary>Adds lines from a worker thread to a log with one UI-thread post per burst, not one per line.</summary>
public sealed class OutputPoster(OutputLog log, IUiDispatcher dispatcher)
{
    private readonly Lock _lock = new();
    private List<OutputLineViewModel> _pending = [];

    public void Add(OutputLineViewModel line)
    {
        bool first;
        lock (_lock)
        {
            _pending.Add(line);
            first = _pending.Count == 1;
        }
        if (first)
            dispatcher.Post(Flush);
    }

    private void Flush()
    {
        List<OutputLineViewModel> lines;
        lock (_lock)
            (lines, _pending) = (_pending, []);
        foreach (var line in lines)
            log.Add(line);
    }
}
