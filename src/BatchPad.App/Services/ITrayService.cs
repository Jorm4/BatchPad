namespace BatchPad.App.Services;

public enum NotificationSeverity { Info, Error }

public interface ITrayService : IDisposable
{
    bool IsVisible { get; set; }

    event Action? OpenRequested;
    event Action? SchedulesRequested;
    event Action? ExitRequested;
    /// <summary>A notification was clicked, closed or timed out.</summary>
    event Action? NotificationClosed;

    void Notify(string title, string message, NotificationSeverity severity, Action onClick);
}
