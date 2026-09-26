using System.Collections.ObjectModel;
using BatchPad.Core.Config;
using BatchPad.Core.Detection;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Wizard;

public enum WizardPage
{
    Folder,
    Scripts,
    Finish,
}

/// <summary>New workspace (§5.1): pick a folder, check the scripts found, then name and trust it.</summary>
public sealed partial class NewWorkspaceWizardViewModel : ObservableObject
{
    private const string DefaultScriptFolder = ".batchpad/scripts";

    private static readonly string[] UncheckedByDefault = ["test_*", "*_test.*", "conftest.py", "__init__.py", "*_lib.py"];

    private readonly MainViewModel _main;

    public NewWorkspaceWizardViewModel(MainViewModel main)
    {
        _main = main;
    }

    public event Action? Closed;

    public ObservableCollection<WizardFolderGroup> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderPage), nameof(IsScriptsPage), nameof(IsFinishPage))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand), nameof(FinishCommand))]
    private WizardPage page;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string folder = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private string name = "";

    [ObservableProperty]
    private bool trustFolder = true;

    [ObservableProperty]
    private string? error;

    public bool IsFolderPage => Page == WizardPage.Folder;
    public bool IsScriptsPage => Page == WizardPage.Scripts;
    public bool IsFinishPage => Page == WizardPage.Finish;

    public IEnumerable<WizardScriptViewModel> Scripts => Groups.SelectMany(g => g.Scripts);

    public string WorkspaceFile => Path.Combine(Folder.Trim(), "batchpad.json");

    [RelayCommand]
    private void Browse()
    {
        if (_main.Services.Dialogs.PickFolder(Folder.Trim().Length > 0 ? Folder.Trim() : Environment.CurrentDirectory) is { } picked)
            Folder = picked;
    }

    private bool CanGoBack() => Page != WizardPage.Folder;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        Error = null;
        Page = Page - 1;
    }

    private bool CanGoNext() => Page switch
    {
        WizardPage.Folder => Folder.Trim().Length > 0,
        WizardPage.Scripts => true,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        Error = null;
        if (Page == WizardPage.Folder)
        {
            var directory = Folder.Trim();
            if (!Directory.Exists(directory))
            {
                Error = "The folder does not exist.";
                return;
            }
            if (File.Exists(WorkspaceFile))
            {
                Error = "This folder already has a batchpad.json. Open it from the workspace list instead.";
                return;
            }
            Scan(directory);
            if (Name.Trim().Length == 0)
                Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
        }
        Page = Page + 1;
    }

    private bool CanFinish() => Page == WizardPage.Finish && Name.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Finish()
    {
        var directory = Path.GetFullPath(Folder.Trim());
        var name = Name.Trim();
        try
        {
            ConfigWriter.Write(WorkspaceFile, new WorkspaceFile
            {
                Schema = Core.Model.WorkspaceFile.SchemaUrl,
                Id = $"{IdAssigner.FromName(name, new HashSet<string>())}-{Guid.NewGuid().ToString("N")[..8]}",
                Name = name,
                ScriptFolders = ScriptFoldersToWrite(),
            });
        }
        catch (IOException ex)
        {
            Error = ex.Message;
            return;
        }
        if (TrustFolder)
            _main.Trust.Trust(directory);
        Closed?.Invoke();
        _main.Open(Path.Combine(directory, "batchpad.json"));
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();

    /// <summary>The default folder, then each proposed folder that has a checked script, excluding its unchecked ones.</summary>
    public List<ScriptFolder> ScriptFoldersToWrite()
    {
        var folders = new List<ScriptFolder> { new() { Path = DefaultScriptFolder } };
        foreach (var group in Groups.Where(g => g.Scripts.Any(s => s.IsChecked)))
        {
            if (group.Folder.Path == DefaultScriptFolder)
                folders.RemoveAt(0);
            var excluded = group.Scripts.Where(s => !s.IsChecked).Select(s => s.PathInFolder).ToList();
            folders.Add(new ScriptFolder
            {
                Path = group.Folder.Path,
                Include = group.Folder.Include,
                Recurse = group.Folder.Recurse,
                Exclude = excluded.Count > 0 ? excluded : null,
            });
        }
        return folders;
    }

    private void Scan(string directory)
    {
        Groups.Clear();
        foreach (var candidate in ProposedFolders(directory))
        {
            var root = ScriptFolderScanner.FullPath(directory, candidate);
            var scripts = ScriptFolderScanner.Scan(directory, [candidate])
                .OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(s => WizardScriptViewModel.For(s, Path.GetRelativePath(root, s.FullPath).Replace('\\', '/'), !IsUncheckedByDefault(s.FullPath)))
                .ToList();
            if (scripts.Count > 0)
                Groups.Add(new WizardFolderGroup(candidate, scripts));
        }
        OnPropertyChanged(nameof(Scripts));
    }

    private static IEnumerable<ScriptFolder> ProposedFolders(string directory)
    {
        yield return new ScriptFolder { Path = ".", Include = ["*.bat"], Recurse = false };
        foreach (var path in new[] { "tools", "scripts", DefaultScriptFolder })
            if (Directory.Exists(Path.Combine(directory, path)))
                yield return new ScriptFolder { Path = path };
    }

    private static bool IsUncheckedByDefault(string path) =>
        UncheckedByDefault.Any(pattern => Glob.IsMatch(pattern, Path.GetFileName(path)));
}

public sealed class WizardFolderGroup(ScriptFolder folder, IReadOnlyList<WizardScriptViewModel> scripts)
{
    public ScriptFolder Folder { get; } = folder;
    public string Title => Folder.Path == "." ? "Project folder" : Folder.Path;
    public IReadOnlyList<WizardScriptViewModel> Scripts { get; } = scripts;
}

public sealed partial class WizardScriptViewModel : ObservableObject
{
    public required string RelativePath { get; init; }
    public required string PathInFolder { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string AutomationId => "WizardScript_" + RelativePath;

    [ObservableProperty]
    private bool isChecked;

    public static WizardScriptViewModel For(DiscoveredScript script, string pathInFolder, bool isChecked)
    {
        var detected = Detector.Detect(script.FullPath);
        return new WizardScriptViewModel
        {
            RelativePath = script.RelativePath,
            PathInFolder = pathInFolder,
            Name = detected.Name,
            Description = detected.Description,
            IsChecked = isChecked,
        };
    }
}
