using BatchPad.App.ViewModels.Editor;
using BatchPad.Core.Config;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Workspace;

/// <summary>"New link…", "New entry…" and "New folder": adds the node to the nearest explicit folder of the tree it was asked from.</summary>
public sealed partial class NewItemViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ScriptTree _tree;
    private readonly FolderNode? _folder;

    public NewItemViewModel(MainViewModel main, NodeViewModel near, NewItemKind kind)
    {
        _main = main;
        _tree = near.Tree;
        Kind = kind;
        _folder = NearestFolder(near);
        Location = _folder is null ? NodeViewModel.RootLabel(_tree.Kind) : $"{NodeViewModel.RootLabel(_tree.Kind)} › {_folder.Folder}";
    }

    /// <summary>The nearest folder with a config entry at or above <paramref name="near"/>; null for the top level.</summary>
    public static FolderNode? NearestFolder(NodeViewModel near)
    {
        for (var node = near; node is not null; node = node.Parent)
            if (node.Item is { HasEntry: true, Node: FolderNode folder })
                return folder;
        return null;
    }

    public NewItemKind Kind { get; }
    public bool IsLink => Kind == NewItemKind.Link;
    public bool IsEntry => Kind == NewItemKind.Entry;
    public bool HasTarget => IsLink || IsEntry;

    public string Title => Kind switch
    {
        NewItemKind.Link => "New link",
        NewItemKind.Entry => "New entry",
        _ => "New folder",
    };

    public string TargetLabel => IsEntry
        ? "Runs (a file relative to the workspace; may use ${param:…}; empty for a module or command)"
        : "Opens (a web address, or a file or folder relative to the workspace)";
    public string Location { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string name = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string target = "";

    [ObservableProperty]
    private string description = "";

    [ObservableProperty]
    private string? error;

    public event Action? Closed;

    [RelayCommand]
    private void Browse()
    {
        if (_main.Services.Dialogs.PickFile(_tree.BaseDirectory) is { } file)
            Target = Path.GetRelativePath(_tree.BaseDirectory, file).Replace('\\', '/');
    }

    private bool CanSave() => Kind switch
    {
        NewItemKind.Link => Name.Trim().Length > 0 && Target.Trim().Length > 0,
        NewItemKind.Entry => Name.Trim().Length > 0 || Target.Trim().Length > 0,
        _ => Name.Trim().Length > 0,
    };

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var folderPath = _folder is null ? null : ConfigEntries.IndexPath(_tree.File.Scripts, _folder);
        var name = Name.Trim();
        var target = Target.Trim();
        var description = GeneralTabViewModel.NullIfEmpty(Description);
        string? id = null;
        try
        {
            ConfigWriter.Update(_tree.FilePath, file =>
            {
                var ids = ConfigEntries.Ids(file.Scripts);
                TreeNode node = Kind switch
                {
                    NewItemKind.Link => new LinkNode
                    {
                        Id = IdAssigner.FromFileName(name, ids), Name = name, Url = target, Description = description,
                    },
                    NewItemKind.Entry => new ScriptNode
                    {
                        Id = id = target.Length > 0 && !target.Contains("${") ? IdAssigner.FromFileName(target, ids) : IdAssigner.FromName(name, ids),
                        Name = GeneralTabViewModel.NullIfEmpty(name),
                        Path = GeneralTabViewModel.NullIfEmpty(target),
                        Description = description,
                    },
                    _ => new FolderNode { Folder = name },
                };
                ConfigEntries.FolderItems(file.Scripts, folderPath).Add(node);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException)
        {
            Error = ex.Message;
            return;
        }
        Closed?.Invoke();
        if (IsEntry)
        {
            _main.Reload(n => n.Tree.Kind == _tree.Kind && n.Node is ScriptNode s && s.Id == id);
            return;
        }
        var kind = IsLink ? NodeKind.Link : NodeKind.Folder;
        _main.Reload(n => n.Tree.Kind == _tree.Kind && n.Kind == kind && n.Name == name);
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();
}
