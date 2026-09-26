using System.Collections.ObjectModel;
using BatchPad.App.ViewModels.Editor;
using BatchPad.Core.Choices;
using BatchPad.Core.Config;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using static BatchPad.App.ViewModels.Editor.GeneralTabViewModel;

namespace BatchPad.App.ViewModels.Workspace;

public sealed partial class KeyValueRow : ObservableObject
{
    [ObservableProperty]
    private string key = "";

    [ObservableProperty]
    private string value = "";
}

public sealed partial class ScriptFolderViewModel : ObservableObject
{
    private readonly string _baseDirectory;
    private readonly Action _changed;

    public ScriptFolderViewModel(ScriptFolder folder, string baseDirectory, Action changed)
    {
        _baseDirectory = baseDirectory;
        path = folder.Path;
        include = string.Join(' ', folder.Include ?? []);
        exclude = string.Join(' ', folder.Exclude ?? []);
        recurse = folder.Recurse != false;
        groupByPrefix = folder.GroupByPrefix;
        _changed = changed;
        RefreshMatches();
    }

    public ObservableCollection<string> Matches { get; } = [];

    [ObservableProperty]
    private string path;

    [ObservableProperty]
    private string include;

    [ObservableProperty]
    private string exclude;

    [ObservableProperty]
    private bool recurse;

    [ObservableProperty]
    private bool groupByPrefix;

    partial void OnPathChanged(string value) => Changed();
    partial void OnIncludeChanged(string value) => Changed();
    partial void OnExcludeChanged(string value) => Changed();
    partial void OnRecurseChanged(bool value) => Changed();
    partial void OnGroupByPrefixChanged(bool value) => Changed();

    public ScriptFolder ToDefinition() => new()
    {
        Path = Path.Trim().Replace('\\', '/'),
        Include = Patterns(Include),
        Exclude = Patterns(Exclude),
        Recurse = Recurse ? null : false,
        GroupByPrefix = GroupByPrefix,
    };

    private static List<string>? Patterns(string text) =>
        text.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } patterns ? [.. patterns] : null;

    private void Changed()
    {
        RefreshMatches();
        _changed();
    }

    private void RefreshMatches()
    {
        Matches.Clear();
        foreach (var script in ScriptFolderScanner.Scan(_baseDirectory, [ToDefinition()]))
            Matches.Add(script.RelativePath);
    }
}

public sealed class SharedParameterViewModel(string key, ParameterDefinition definition, ChoiceEnvironment environment,
    IReadOnlyList<string> usedBy, Action changed)
{
    public string Key { get; } = key;
    public ParameterDefinition Definition { get; } = definition;
    public ParameterEditorViewModel Editor { get; } = new(definition, changed);
    public ChoicesTabViewModel Choices { get; } = new(definition, environment, changed);
    public IReadOnlyList<string> UsedBy { get; } = usedBy;
    public string UsedBySummary => UsedBy.Count == 0 ? "Not used by any script yet." : $"Used by {string.Join(", ", UsedBy)}";
}

/// <summary>The workspace page (§5.1): edits a copy of the workspace file's settings and writes them on Save.</summary>
public sealed partial class WorkspaceSettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ScriptTree _tree;
    private readonly ChoiceEnvironment _choiceEnvironment;
    private bool _foldersChanged;

    public WorkspaceSettingsViewModel(MainViewModel main)
    {
        _main = main;
        _tree = main.Workspace!.Workspace;
        var file = ConfigEntries.Clone(_tree.File);
        var resolver = new ChoiceResolver();
        var context = new ChoiceContext(_tree.BaseDirectory) { Lists = file.Lists };
        _choiceEnvironment = new ChoiceEnvironment(_tree.BaseDirectory, p => resolver.Resolve(p, context), main.Services.Dialogs);

        name = file.Name ?? "";
        foreach (var folder in file.ScriptFolders ?? ScriptFolderScanner.DefaultFolders)
            ScriptFolders.Add(NewFolder(folder));
        foreach (var (key, definition) in file.SharedParams ?? [])
            SharedParameters.Add(NewShared(key, definition));
        selectedShared = SharedParameters.FirstOrDefault();
        AddRows(Variables, file.Variables);
        AddRows(Environment, file.Env);
    }

    public ObservableCollection<ScriptFolderViewModel> ScriptFolders { get; } = [];
    public ObservableCollection<SharedParameterViewModel> SharedParameters { get; } = [];
    public ObservableCollection<KeyValueRow> Variables { get; } = [];
    public ObservableCollection<KeyValueRow> Environment { get; } = [];
    public string FileName => Path.GetFileName(_tree.FilePath);

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private SharedParameterViewModel? selectedShared;

    [ObservableProperty]
    private string newSharedName = "";

    [ObservableProperty]
    private string? error;

    public event Action? Closed;

    public ScriptFolderViewModel? Folder(string path) => ScriptFolders.FirstOrDefault(f => f.Path == path);

    public SharedParameterViewModel? Shared(string key) => SharedParameters.FirstOrDefault(s => s.Key == key);

    [RelayCommand]
    private void AddFolder()
    {
        ScriptFolders.Add(NewFolder(new ScriptFolder()));
        _foldersChanged = true;
    }

    [RelayCommand]
    private void RemoveFolder(ScriptFolderViewModel folder)
    {
        ScriptFolders.Remove(folder);
        _foldersChanged = true;
    }

    [RelayCommand]
    private void BrowseFolder(ScriptFolderViewModel folder)
    {
        if (_main.Services.Dialogs.PickFolder(_tree.BaseDirectory) is { } picked)
            folder.Path = Path.GetRelativePath(_tree.BaseDirectory, picked).Replace('\\', '/');
    }

    [RelayCommand]
    private void AddShared()
    {
        var key = NewSharedName.Trim();
        if (key.Length == 0 || Shared(key) is not null)
            return;
        var shared = NewShared(key, new ParameterDefinition { Type = ParameterType.Choice });
        SharedParameters.Add(shared);
        SelectedShared = shared;
        NewSharedName = "";
    }

    [RelayCommand]
    private void RemoveShared(SharedParameterViewModel shared) => SharedParameters.Remove(shared);

    [RelayCommand]
    private void AddVariable() => Variables.Add(new KeyValueRow());

    [RelayCommand]
    private void RemoveVariable(KeyValueRow row) => Variables.Remove(row);

    [RelayCommand]
    private void AddEnvironment() => Environment.Add(new KeyValueRow());

    [RelayCommand]
    private void RemoveEnvironment(KeyValueRow row) => Environment.Remove(row);

    [RelayCommand]
    private void Save()
    {
        var folders = _foldersChanged ? ScriptFolders.Where(f => f.Path.Trim().Length > 0).Select(f => f.ToDefinition()).ToList() : null;
        var shared = SharedParameters.ToDictionary(s => s.Key, s => ConfigEntries.Clone(s.Definition));
        try
        {
            ConfigWriter.Update(_tree.FilePath, file =>
            {
                file.Name = NullIfEmpty(Name);
                if (folders is not null)
                    file.ScriptFolders = folders;
                file.SharedParams = shared.Count == 0 ? null : shared;
                file.Variables = ToDictionary(Variables);
                file.Env = ToDictionary(Environment);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException)
        {
            Error = ex.Message;
            return;
        }
        Closed?.Invoke();
        _main.Reload();
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();

    private ScriptFolderViewModel NewFolder(ScriptFolder folder) =>
        new(folder, _tree.BaseDirectory, () => _foldersChanged = true);

    private SharedParameterViewModel NewShared(string key, ParameterDefinition definition) =>
        new(key, definition, _choiceEnvironment, UsersOf(key), () => { });

    private List<string> UsersOf(string key) =>
        _main.Workspace!.Trees
            .SelectMany(t => t.AllNodes())
            .Where(n => n.Node is RunnableNode { Params: { } parameters } && parameters.Any(p => p.Use == key))
            .Select(n => n.Location)
            .ToList();

    private static void AddRows(ObservableCollection<KeyValueRow> rows, Dictionary<string, string>? values)
    {
        foreach (var (key, value) in values ?? [])
            rows.Add(new KeyValueRow() { Key = key, Value = value });
    }

    private static Dictionary<string, string>? ToDictionary(IEnumerable<KeyValueRow> rows)
    {
        var result = new Dictionary<string, string>();
        foreach (var row in rows.Where(r => r.Key.Trim().Length > 0))
            result[row.Key.Trim()] = row.Value;
        return result.Count == 0 ? null : result;
    }
}
