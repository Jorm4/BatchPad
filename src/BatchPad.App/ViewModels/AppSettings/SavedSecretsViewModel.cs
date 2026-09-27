using System.Collections.ObjectModel;
using System.ComponentModel;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.AppSettings;

/// <summary>The secrets saved for unattended runs of the open workspace and of Global scripts, by name only.</summary>
public sealed partial class SavedSecretsViewModel : ObservableObject
{
    private readonly ISecretStore _store;

    public SavedSecretsViewModel(ISecretStore store, LoadedWorkspace? workspace)
    {
        _store = store;
        try
        {
            if (workspace is not null)
                Add(SecretScope.Of(workspace), "This workspace");
            Add(SecretScope.Global, "Global");
        }
        catch (Win32Exception ex)
        {
            Error = ex.Message;
        }
    }

    public ObservableCollection<SavedSecretViewModel> Secrets { get; } = [];

    public bool HasSecrets => Secrets.Count > 0;

    [ObservableProperty]
    private string? error;

    private void Add(SecretScope scope, string scopeLabel)
    {
        foreach (var name in scope.Names(_store))
            Secrets.Add(new SavedSecretViewModel(this, name, scopeLabel, scope));
    }

    internal void Remove(SavedSecretViewModel secret)
    {
        try
        {
            secret.SavedIn.Remove(_store, secret.Name);
            Secrets.Remove(secret);
            OnPropertyChanged(nameof(HasSecrets));
        }
        catch (Win32Exception ex)
        {
            Error = ex.Message;
        }
    }
}

public sealed partial class SavedSecretViewModel(SavedSecretsViewModel owner, string name, string scope, SecretScope savedIn)
{
    public string Name { get; } = name;
    public string Scope { get; } = scope;
    public SecretScope SavedIn { get; } = savedIn;

    [RelayCommand]
    private void Remove() => owner.Remove(this);
}
