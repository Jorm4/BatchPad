namespace BatchPad.Core.Workspace;

/// <summary>Where settings, the global library and My Scripts live (§3.8), and machine-local data such as history (§3.9).</summary>
public sealed class AppPaths(string dataDirectory, bool isPortable, string? localDirectory = null)
{
    public const string PortableMarker = "batchpad.portable";

    public string DataDirectory { get; } = dataDirectory;
    public bool IsPortable { get; } = isPortable;
    public string LocalDirectory { get; } = localDirectory ?? Path.Combine(dataDirectory, "local");

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string GlobalFile => Path.Combine(DataDirectory, "global.json");
    public string UserFile(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId, "user.json");

    public static AppPaths Resolve(string exeDirectory, string appDataDirectory, string localAppDataDirectory) =>
        File.Exists(Path.Combine(exeDirectory, PortableMarker))
            ? new AppPaths(Path.Combine(exeDirectory, "data"), isPortable: true)
            : new AppPaths(Path.Combine(appDataDirectory, "BatchPad"), isPortable: false,
                Path.Combine(localAppDataDirectory, "BatchPad"));

    public static AppPaths ForCurrentProcess() =>
        Resolve(AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}
