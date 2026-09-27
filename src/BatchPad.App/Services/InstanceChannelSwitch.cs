namespace BatchPad.App.Services;

/// <summary>Keeps an <see cref="InstanceChannel"/> serving whichever workspace is open.</summary>
public sealed class InstanceChannelSwitch(string exePath, Func<string?> openWorkspace, Action<Action> post, Action<string> run,
    Action<Exception> onError) : IDisposable
{
    private InstanceChannel? _channel;

    public string? PipeName => _channel?.PipeName;

    public void Update()
    {
        var file = openWorkspace();
        var name = file is null ? null : InstanceChannel.PipeNameFor(exePath, file);
        if (name == _channel?.PipeName)
            return;
        _channel?.Dispose();
        _channel = name is null ? null : new InstanceChannel(name, key => post(() =>
        {
            // A request sent to the previous workspace can still be queued after a switch.
            if (string.Equals(openWorkspace(), file, StringComparison.OrdinalIgnoreCase))
                run(key);
        }), onError);
    }

    public void Dispose() => _channel?.Dispose();
}
