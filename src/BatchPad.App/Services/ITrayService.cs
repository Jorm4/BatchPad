namespace BatchPad.App.Services;

public interface ITrayService : IDisposable
{
    bool IsVisible { get; set; }

    event Action? OpenRequested;
    event Action? SchedulesRequested;
    event Action? ExitRequested;

    void Notify(string title, string message, Action onClick);
}
