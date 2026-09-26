using System.ComponentModel;
using BatchPad.Core.Config;
using BatchPad.Core.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.AppSettings;

/// <summary>One forwarding target on the Telemetry section; every committed edit is saved straight away.</summary>
public sealed partial class SinkViewModel : ObservableObject
{
    public const string SecretWarningText =
        "This looks like a secret typed in; settings.json would keep it as plain text. Use ${env:NAME} and set the variable instead.";

    private static readonly string[] SecretHeaderWords = ["auth", "key", "token", "secret", "password"];

    private readonly TelemetrySettingsViewModel _owner;
    private readonly SinkConfig _original;

    public SinkViewModel(TelemetrySettingsViewModel owner, SinkConfig config)
    {
        _owner = owner;
        _original = ConfigJson.Clone(config);
        Type = config.Type;
        enabled = config.Enabled;
        path = config.Path;
        maxSizeMb = config.MaxSizeMb?.ToString();
        endpoint = config.Endpoint;
        url = config.Url;
        index = config.Index;
        apiKey = config.ApiKey;
        org = config.Org;
        bucket = config.Bucket;
        token = config.Token;
        headersText = config.Headers is null ? "" : string.Join(Environment.NewLine, config.Headers.Select(h => $"{h.Key}: {h.Value}"));
        PropertyChanged += OnEdited;
    }

    public string Type { get; }

    public bool IsJsonl => Type == SinkTypes.Jsonl;
    public bool IsOtlp => Type == SinkTypes.Otlp;
    public bool IsElastic => Type == SinkTypes.Elastic;
    public bool IsInflux => Type == SinkTypes.Influx;
    public bool UsesUrl => Type is SinkTypes.Elastic or SinkTypes.Influx or SinkTypes.Http;
    public bool UsesHeaders => Type is SinkTypes.Otlp or SinkTypes.Http;

    [ObservableProperty]
    private bool enabled;

    [ObservableProperty]
    private string? path;

    [ObservableProperty]
    private string? maxSizeMb;

    [ObservableProperty]
    private string? endpoint;

    [ObservableProperty]
    private string? url;

    [ObservableProperty]
    private string? index;

    [ObservableProperty]
    private string? apiKey;

    [ObservableProperty]
    private string? org;

    [ObservableProperty]
    private string? bucket;

    [ObservableProperty]
    private string? token;

    [ObservableProperty]
    private string headersText;

    [ObservableProperty]
    private SinkStatus? status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendTestEventCommand))]
    private bool isSending;

    [ObservableProperty]
    private string? testResult;

    [ObservableProperty]
    private bool testFailed;

    public string Title => $"{Type} · {(Target.Length == 0 ? "not set up yet" : Target)}";

    private string Target => ToConfig().Target;

    public string? SecretWarning => HasLiteralSecret() ? SecretWarningText : null;

    public string StatusText
    {
        get
        {
            if (Status is not { } s)
                return "";
            var sent = s.LastSuccess is { } success ? $"Last sent {success.ToLocalTime():g}" : "Nothing sent yet";
            return $"{sent} · {s.Pending} pending";
        }
    }

    public string? LastErrorText =>
        Status is { LastError: { } error, LastErrorAt: { } at } ? $"{at.ToLocalTime():g}: {error}" : null;

    public bool IsFailing => Status?.IsFailing == true;

    public SinkConfig ToConfig()
    {
        var config = ConfigJson.Clone(_original);
        config.Enabled = Enabled;
        config.Path = Blank(Path);
        config.MaxSizeMb = int.TryParse(MaxSizeMb, out var size) && size > 0 ? size : null;
        config.Endpoint = Blank(Endpoint);
        config.Url = Blank(Url);
        config.Index = Blank(Index);
        config.ApiKey = Blank(ApiKey);
        config.Org = Blank(Org);
        config.Bucket = Blank(Bucket);
        config.Token = Blank(Token);
        config.Headers = ParseHeaders(HeadersText);
        return config;
    }

    /// <summary>Anything typed into a credential that isn't an <c>${env:…}</c> reference.</summary>
    public static bool LooksLikeSecret(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains("${env:", StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanSendTestEvent))]
    private async Task SendTestEventAsync()
    {
        IsSending = true;
        TestResult = "Sending…";
        TestFailed = false;
        try
        {
            var error = await _owner.SendTestEventAsync(ToConfig());
            TestFailed = error is not null;
            TestResult = error is null ? "Test event sent." : $"Failed: {error}";
        }
        finally
        {
            IsSending = false;
        }
    }

    private bool CanSendTestEvent() => !IsSending;

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    partial void OnStatusChanged(SinkStatus? value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastErrorText));
        OnPropertyChanged(nameof(IsFailing));
    }

    private void OnEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Enabled) or nameof(Path) or nameof(MaxSizeMb) or nameof(Endpoint) or nameof(Url)
            or nameof(Index) or nameof(ApiKey) or nameof(Org) or nameof(Bucket) or nameof(Token) or nameof(HeadersText)))
            return;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SecretWarning));
        _owner.Save();
    }

    private bool HasLiteralSecret() =>
        LooksLikeSecret(ApiKey) || LooksLikeSecret(Token)
        || (ParseHeaders(HeadersText) ?? []).Any(h =>
            SecretHeaderWords.Any(word => h.Key.Contains(word, StringComparison.OrdinalIgnoreCase)) && LooksLikeSecret(h.Value));

    private static Dictionary<string, string>? ParseHeaders(string? text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? "").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim() is { Length: > 0 } name)
                headers[name] = line[(colon + 1)..].Trim();
        }
        return headers.Count == 0 ? null : headers;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
