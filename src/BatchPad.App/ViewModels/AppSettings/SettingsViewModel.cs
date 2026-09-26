using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.AppSettings;

/// <summary>BatchPad's own settings (settings.json), as opposed to a workspace's.</summary>
public sealed partial class SettingsViewModel(MainViewModel main)
{
    public TelemetrySettingsViewModel Telemetry { get; } = new(main);

    public event Action? Closed;

    public void Detach() => Telemetry.Detach();

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
