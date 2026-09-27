using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BatchPad.App.Services;

/// <summary>Lets <c>BatchPad.exe --run</c> hand a script to the instance that already has its workspace open.</summary>
public sealed class InstanceChannel : IDisposable
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    private const int MaxRequestLength = 4096;

    private readonly CancellationTokenSource _stop = new();

    public InstanceChannel(string pipeName, Action<string> onRun, Action<Exception> onError)
    {
        PipeName = pipeName;
        _ = Task.Run(() => ServeAsync(onRun, onError, _stop.Token));
    }

    public string PipeName { get; }

    /// <summary>Per user and per exe, so a stable and a dev build each keep their own instances.</summary>
    public static string PipeNameFor(string exePath, string workspaceFile)
    {
        var identity = $"{Path.GetFullPath(exePath)}|{Path.GetFullPath(workspaceFile)}".ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $"BatchPad-{WindowsIdentity.GetCurrent().User?.Value}-{hash}";
    }

    /// <summary>False when no instance answered within <see cref="ConnectTimeout"/>.</summary>
    public static bool TrySend(string pipeName, string nodeKey)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(ConnectTimeout);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(new JsonObject { ["run"] = nodeKey }.ToJsonString());
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string? ParseRequest(string? line)
    {
        if (line is null || line.Length > MaxRequestLength)
            return null;
        try
        {
            return JsonNode.Parse(line) is JsonObject request && request["run"] is JsonValue value && value.TryGetValue<string>(out var key)
                && key.Length > 0
                ? key
                : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private async Task ServeAsync(Action<string> onRun, Action<Exception> onError, CancellationToken token)
    {
        try
        {
            await ServeEachAsync(onRun, onError, token).ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task ServeEachAsync(Action<string> onRun, Action<Exception> onError, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                if (ParseRequest(await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)) is { } key)
                    onRun(key);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onError(ex);
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _stop.Cancel();
}
