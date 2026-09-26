namespace BatchPad.Core.Workspace;

/// <summary>Where settings, the global library and My Scripts live (§3.8).</summary>
public sealed class AppPaths(string dataDirectory, bool isPortable)
{
    public const string PortableMarker = "batchpad.portable";

    public string DataDirectory { get; } = dataDirectory;
    public bool IsPortable { get; } = isPortable;

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string GlobalFile => Path.Combine(DataDirectory, "global.json");
    public string UserFile(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId, "user.json");

    public static AppPaths Resolve(string exeDirectory, string appDataDirectory) =>
        File.Exists(Path.Combine(exeDirectory, PortableMarker))
            ? new AppPaths(Path.Combine(exeDirectory, "data"), isPortable: true)
            : new AppPaths(Path.Combine(appDataDirectory, "BatchPad"), isPortable: false);

    public static AppPaths ForCurrentProcess() =>
        Resolve(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
}
