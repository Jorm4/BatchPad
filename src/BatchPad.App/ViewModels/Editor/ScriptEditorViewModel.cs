using BatchPad.Core.Choices;
using BatchPad.Core.Config;
using BatchPad.Core.Detection;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Editor;

/// <summary>Edits a copy of a script's definition (§5.1); nothing is written until <see cref="SaveCommand"/>.</summary>
public sealed partial class ScriptEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly NodeViewModel _node;
    private readonly ScriptNode _original;

    public ScriptEditorViewModel(MainViewModel main, NodeViewModel node)
    {
        _main = main;
        _node = node;
        _original = node.Script!;
        Definition = ConfigEntries.Clone(_original);
        General = new GeneralTabViewModel(Definition, node.Name, Refresh);
        Parameters = new ParametersTabViewModel(Definition, main.Workspace!.Workspace.File.SharedParams?.Keys ?? Enumerable.Empty<string>(),
            DetectParameters(), Refresh);
        AfterRun = new AfterRunTabViewModel(Definition, StopChoices(main, node), Refresh);
        Advanced = new AdvancedTabViewModel(Definition, Refresh);
        var resolver = new ChoiceResolver();
        var context = RunPlanner.ChoicesFor(Request());
        ChoiceEnvironment = new ChoiceEnvironment(context.BaseDirectory, p => resolver.Resolve(p, context), main.Services.Dialogs);
        Parameters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ParametersTabViewModel.Selected))
                Choices = BuildChoices();
        };
        choices = BuildChoices();
        Refresh();
    }

    public ChoiceEnvironment ChoiceEnvironment { get; }

    /// <summary>The Choices tab for the selected parameter; null for a <c>use</c> entry, whose choices live in the shared definition.</summary>
    [ObservableProperty]
    private ChoicesTabViewModel? choices;

    private ChoicesTabViewModel? BuildChoices() =>
        Parameters.Selected is { IsShared: false } selected ? new ChoicesTabViewModel(selected.Definition, ChoiceEnvironment, Refresh) : null;

    public ScriptNode Definition { get; }
    public GeneralTabViewModel General { get; }
    public ParametersTabViewModel Parameters { get; }
    public AfterRunTabViewModel AfterRun { get; }
    public AdvancedTabViewModel Advanced { get; }
    public string Title => _node.Name;

    public bool IsShared => _node.Tree.Kind != TreeKind.MyScripts;

    public string SharedBannerText => _node.Tree.Kind == TreeKind.Workspace
        ? $"Shared script — saved to {Path.GetFileName(_node.Tree.FilePath)} in the repository. Everyone who pulls gets this change."
        : "Shared script — saved to the global library. Every workspace on this computer gets this change.";

    [ObservableProperty]
    private string preview = "";

    [ObservableProperty]
    private string? error;

    /// <summary>Raised when the editor should close; true after a save.</summary>
    public event Action<bool>? Closed;

    public RunRequest Request() => new(_main.Workspace!, _node.Tree, Definition);

    public void Refresh()
    {
        try
        {
            Preview = string.Join(Environment.NewLine,
                RunPlanner.Plan(Request(), _main.Services.Interpreters).Select(s => s.Command.DisplayRelativeTo(_main.Workspace!.Directory)));
        }
        catch (Exception ex) when (DetailsViewModel.IsRunProblem(ex))
        {
            Preview = $"⚠ {ex.Message}";
        }
        TestRunCommand.NotifyCanExecuteChanged();
    }

    private bool CanTestRun() => _main.IsTrusted;

    [RelayCommand(CanExecute = nameof(CanTestRun))]
    private void TestRun() =>
        _main.Details.Launch(new RunRequest(_main.Workspace!, _node.Tree, ConfigEntries.Clone(Definition)), _node);

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(false);

    [RelayCommand]
    private void Save()
    {
        var tree = _node.Tree;
        var indexPath = _node.Item?.HasEntry == true ? ConfigEntries.IndexPath(tree.File.Scripts, _original) : null;
        ScriptNode saved = null!;
        try
        {
            ConfigWriter.Update(tree.FilePath, file =>
            {
                saved = ConfigEntries.Clone(Definition);
                if (!ConfigEntries.Replace(file.Scripts, indexPath, _original, saved))
                {
                    saved.Id ??= IdAssigner.FromFileName(saved.Path ?? saved.Name ?? "script", ConfigEntries.Ids(file.Scripts));
                    file.Scripts.Add(saved);
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException)
        {
            Error = ex.Message;
            return;
        }
        Closed?.Invoke(true);
        _main.Reload(n => n.Tree.Kind == tree.Kind && n.Script is { } s
            && (saved.Id is not null ? s.Id == saved.Id : s.Path == saved.Path));
    }

    /// <summary>Scripts with an id that could stop this one, as references it can resolve.</summary>
    private static IReadOnlyList<EditorOption<string?>> StopChoices(MainViewModel main, NodeViewModel self) =>
    [
        new(null, "None — close its windows, then end it"),
        .. main.Tree!.AllNodes
            .Where(n => n != self && n.Node is ScriptNode { Id: not null } && (n.Tree.Kind == self.Tree.Kind || n.Tree.Kind != TreeKind.MyScripts))
            .Select(n => new EditorOption<string?>(
                n.Tree.Kind == self.Tree.Kind ? ((ScriptNode)n.Node!).Id : $"{n.Tree.Kind.ToString().ToLowerInvariant()}:{((ScriptNode)n.Node!).Id}",
                $"{n.Location} › {n.Name}")),
    ];

    private IEnumerable<ParameterDefinition> DetectParameters()
    {
        if (Definition.Path is not { } path || path.Contains("${"))
            return [];
        var fullPath = Path.Combine(_node.Tree.BaseDirectory, path);
        try
        {
            return File.Exists(fullPath) ? Detector.Detect(fullPath).Parameters : [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
