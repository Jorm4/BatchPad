using System.Windows;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.Views;

namespace BatchPad.App.Services;

/// <summary>Asks for the values a run prompts for (<c>ask</c> and <c>secret</c> parameters).</summary>
public interface IAskService
{
    /// <returns>False when the user cancels the run.</returns>
    bool Ask(string title, ParameterFormViewModel form);
}

public sealed class AskDialogService : IAskService
{
    public bool Ask(string title, ParameterFormViewModel form) =>
        new AskDialog { Title = title, DataContext = form, Owner = Application.Current?.MainWindow }.ShowDialog() == true;
}
