using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchPad.App.ViewModels.Editor;

/// <summary>The editor's Advanced tab (§5.1): fixed arguments and the name template for new My Scripts entries.</summary>
public sealed partial class AdvancedTabViewModel : ObservableObject
{
    private readonly ScriptNode _definition;
    private readonly Action _changed;

    public AdvancedTabViewModel(ScriptNode definition, Action changed)
    {
        _definition = definition;
        _changed = changed;
        fixedArgs = definition.Args is { } args ? ArgvQuoter.Join(args) : "";
        nameTemplate = definition.NameTemplate ?? "";
        id = definition.Id ?? "";
    }

    [ObservableProperty]
    private string id;

    partial void OnIdChanged(string value)
    {
        _definition.Id = GeneralTabViewModel.NullIfEmpty(value);
        _changed();
    }

    /// <summary>Arguments passed before the parameters' own, split like a command line.</summary>
    [ObservableProperty]
    private string fixedArgs;

    [ObservableProperty]
    private string nameTemplate;

    partial void OnFixedArgsChanged(string value)
    {
        var args = ArgumentAssembler.SplitArguments(value);
        _definition.Args = args.Count == 0 ? null : args;
        _changed();
    }

    partial void OnNameTemplateChanged(string value)
    {
        _definition.NameTemplate = GeneralTabViewModel.NullIfEmpty(value);
        _changed();
    }
}
