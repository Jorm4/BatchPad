using System.Collections.ObjectModel;
using System.ComponentModel;
using BatchPad.App.Services;
using BatchPad.App.ViewModels.Workspace;
using BatchPad.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Editor;

/// <summary>The editor's Environment tab (§5.1): the script's <c>env</c> and <c>envFile</c>.</summary>
public sealed partial class EnvironmentTabViewModel : ObservableObject
{
    private readonly ScriptNode _definition;
    private readonly string _baseDirectory;
    private readonly IFileDialogService _dialogs;
    private readonly Action _changed;

    public EnvironmentTabViewModel(ScriptNode definition, string baseDirectory, IFileDialogService dialogs, Action changed)
    {
        _definition = definition;
        _baseDirectory = baseDirectory;
        _dialogs = dialogs;
        _changed = changed;
        envFile = definition.EnvFile ?? "";
        foreach (var (key, value) in definition.Env ?? [])
            Add(new KeyValueRow { Key = key, Value = value });
    }

    public ObservableCollection<KeyValueRow> Variables { get; } = [];

    [ObservableProperty]
    private string envFile;

    partial void OnEnvFileChanged(string value)
    {
        _definition.EnvFile = GeneralTabViewModel.NullIfEmpty(value);
        _changed();
    }

    [RelayCommand]
    private void BrowseEnvFile()
    {
        if (_dialogs.PickFile(_baseDirectory) is { } file)
            EnvFile = Path.GetRelativePath(_baseDirectory, file).Replace('\\', '/');
    }

    [RelayCommand]
    private void AddVariable()
    {
        Add(new KeyValueRow());
        Write();
    }

    [RelayCommand]
    private void RemoveVariable(KeyValueRow? row)
    {
        if (row is null || !Variables.Remove(row))
            return;
        row.PropertyChanged -= OnRowChanged;
        Write();
    }

    private void Add(KeyValueRow row)
    {
        row.PropertyChanged += OnRowChanged;
        Variables.Add(row);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Write();

    private void Write()
    {
        _definition.Env = KeyValueRow.ToDictionary(Variables);
        _changed();
    }
}
