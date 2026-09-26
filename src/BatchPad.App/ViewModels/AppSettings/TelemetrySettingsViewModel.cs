using System.Collections.ObjectModel;
using BatchPad.Core.Config;
using BatchPad.Core.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.AppSettings;

/// <summary>The privacy switches and sinks of settings.json's <c>telemetry</c> block (§4.4), with each sink's delivery status.</summary>
public sealed partial class TelemetrySettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _loading = true;

    public TelemetrySettingsViewModel(MainViewModel main)
    {
        _main = main;
        var options = main.UserSettings.Telemetry ?? new TelemetryOptions();
        machine = options.Machine;
        user = options.User;
        includeValues = options.IncludeValues;
        hashNames = options.HashNames;
        foreach (var sink in options.Sinks)
            Sinks.Add(new SinkViewModel(this, sink));
        _loading = false;
        _main.Telemetry.StatusChanged += OnStatusChanged;
        RefreshStatus();
    }

    public static IReadOnlyList<string> AvailableSinkTypes => SinkTypes.All;

    public ObservableCollection<SinkViewModel> Sinks { get; } = [];

    public bool HasSinks => Sinks.Count > 0;

    [ObservableProperty]
    private bool machine;

    [ObservableProperty]
    private bool user;

    [ObservableProperty]
    private bool includeValues;

    [ObservableProperty]
    private bool hashNames;

    [ObservableProperty]
    private string newSinkType = SinkTypes.Jsonl;

    public void Save()
    {
        if (_loading)
            return;
        _main.UserSettings.Update(_main.Paths.SettingsFile, s => s.Telemetry = ToOptions());
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var statuses = _main.Telemetry.Statuses().ToDictionary(s => s.Key);
        foreach (var sink in Sinks)
            sink.Status = statuses.GetValueOrDefault(sink.ToConfig().Key);
    }

    public void Detach() => _main.Telemetry.StatusChanged -= OnStatusChanged;

    internal Task<string?> SendTestEventAsync(SinkConfig sink) => _main.Telemetry.SendTestEventAsync(sink);

    internal void Remove(SinkViewModel sink)
    {
        Sinks.Remove(sink);
        OnPropertyChanged(nameof(HasSinks));
        Save();
    }

    [RelayCommand]
    private void AddSink()
    {
        Sinks.Add(new SinkViewModel(this, new SinkConfig { Type = NewSinkType }));
        OnPropertyChanged(nameof(HasSinks));
        Save();
    }

    [RelayCommand]
    private void OpenOutboxFolder()
    {
        try
        {
            Directory.CreateDirectory(_main.Telemetry.Outbox.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        _main.Services.Shell.Open(_main.Telemetry.Outbox.Directory);
    }

    partial void OnMachineChanged(bool value) => Save();

    partial void OnUserChanged(bool value) => Save();

    partial void OnIncludeValuesChanged(bool value) => Save();

    partial void OnHashNamesChanged(bool value) => Save();

    private TelemetryOptions ToOptions()
    {
        var options = _main.UserSettings.Telemetry is { } current ? ConfigJson.Clone(current) : new TelemetryOptions();
        options.Machine = Machine;
        options.User = User;
        options.IncludeValues = IncludeValues;
        options.HashNames = HashNames;
        options.Sinks = [.. Sinks.Select(s => s.ToConfig())];
        return options;
    }

    private void OnStatusChanged() => _main.Services.Dispatcher.Post(RefreshStatus);
}
