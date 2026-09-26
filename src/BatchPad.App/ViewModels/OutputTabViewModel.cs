using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public abstract partial class OutputTabViewModel(string title, NodeViewModel? node) : ObservableObject, IDisposable
{
    public string Title { get; } = title;
    public NodeViewModel? Node { get; set; } = node;

    /// <summary>Completes once the result is shown.</summary>
    public Task Finished { get; protected set; } = Task.CompletedTask;

    public abstract bool IsRunning { get; }
    public abstract bool Succeeded { get; }
    public abstract string StatusText { get; }
    public abstract string DurationText { get; }

    [ObservableProperty]
    private bool autoScroll = true;

    public virtual OutputLog? Log => null;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task Stop() => StopRunAsync();

    protected abstract Task StopRunAsync();

    public virtual void Dispose()
    {
    }

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 60 ? $"{duration.TotalSeconds:0.0} s" : $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s";
}
