using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.AppSettings;

/// <summary>BatchPad's own settings (settings.json), as opposed to a workspace's.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Telemetry = new TelemetrySettingsViewModel(main);
        SavedSecrets = new SavedSecretsViewModel(main.Services.Secrets, main.Workspace);
        mcpEnabled = main.UserSettings.Mcp?.Enabled == true;
    }

    public TelemetrySettingsViewModel Telemetry { get; }
    public SavedSecretsViewModel SavedSecrets { get; }

    [ObservableProperty]
    private bool mcpEnabled;

    public event Action? Closed;

    public void Detach() => Telemetry.Detach();

    partial void OnMcpEnabledChanged(bool value) =>
        _main.UserSettings.Update(_main.Paths.SettingsFile, s => (s.Mcp ??= new McpSettings()).Enabled = value);

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
