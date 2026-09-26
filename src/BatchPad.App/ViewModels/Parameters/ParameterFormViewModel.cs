using System.Text.Json.Nodes;
using BatchPad.App.Services;
using BatchPad.Core.Arguments;
using BatchPad.Core.Choices;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchPad.App.ViewModels.Parameters;

public sealed record ParameterValues(IReadOnlyDictionary<string, JsonNode?> Values, string ExtraArguments);

public sealed partial class ParameterFormViewModel : ObservableObject
{
    public ParameterFormViewModel(IReadOnlyList<ParameterDefinition> parameters, Func<ParameterDefinition, ResolvedChoices> resolveChoices,
        ParameterValues? stored, IFileDialogService dialogs, string baseDirectory)
    {
        Fields = parameters.Where(p => p.Name is not null).Select(p =>
        {
            JsonNode? value = null;
            var isSet = stored?.Values.TryGetValue(p.Name!, out value) == true;
            ParameterFieldViewModel field = p.Type switch
            {
                ParameterType.Flag => new FlagFieldViewModel(p, value, isSet),
                ParameterType.Choice => new ChoiceFieldViewModel(p, resolveChoices(p), value, isSet),
                ParameterType.Multichoice => new MultichoiceFieldViewModel(p, resolveChoices(p), value, isSet),
                ParameterType.Int => new IntFieldViewModel(p, value, isSet),
                ParameterType.Path => new PathFieldViewModel(p, value, isSet, dialogs, baseDirectory),
                ParameterType.Secret => new SecretFieldViewModel(p, value, isSet),
                _ => new TextFieldViewModel(p, value, isSet),
            };
            field.Changed += OnFieldChanged;
            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ParameterFieldViewModel.Error))
                    OnPropertyChanged(nameof(ErrorSummary));
            };
            return field;
        }).ToList();
        extraArguments = stored?.ExtraArguments ?? "";
    }

    /// <summary>The form for a script's or workflow's parameters, with shared <c>use</c> entries merged in.</summary>
    /// <exception cref="ArgumentAssemblyException">A <c>use</c> names no shared parameter.</exception>
    public static ParameterFormViewModel For(LoadedWorkspace workspace, RunnableNode definition, ScriptTree tree, ParameterValues? stored,
        IFileDialogService dialogs, CommandChoiceSource? commands, Func<ParameterDefinition, bool>? include = null)
    {
        var parameters = SharedParameters.MergeAll(definition.Params, workspace.Workspace.File.SharedParams);
        var context = definition is ScriptNode script
            ? RunPlanner.ChoicesFor(new RunRequest(workspace, tree, script))
            : new ChoiceContext(workspace.Directory)
            {
                Lists = workspace.Workspace.File.Lists,
                Templates = new TemplateContext { WorkspaceDir = workspace.Directory, Variables = workspace.Workspace.File.Variables },
            };
        context = context with { Commands = commands };
        var choices = new ChoiceResolver();
        return new ParameterFormViewModel(parameters.Where(p => include?.Invoke(p) != false).ToList(), p => choices.Resolve(p, context),
            stored, dialogs, workspace.Directory);
    }

    public IReadOnlyList<ParameterFieldViewModel> Fields { get; }

    public bool HasExtraArguments { get; set; } = true;

    [ObservableProperty]
    private string extraArguments;

    public event Action? Changed;

    public bool HasErrors => Fields.Any(f => f.Error is not null);

    public string? ErrorSummary => Fields.FirstOrDefault(f => f.Error is not null) is { } invalid ? $"{invalid.Label}: {invalid.Error}" : null;

    public IReadOnlyDictionary<string, JsonNode?> Values => Fields.Where(f => f.IsSet).ToDictionary(f => f.Name, f => f.Value);

    /// <summary>The values to keep, which leave out secrets.</summary>
    public ParameterValues Snapshot() =>
        new(Fields.Where(f => f.IsSet && f is not SecretFieldViewModel).ToDictionary(f => f.Name, f => f.Value), ExtraArguments);

    public ParameterFieldViewModel? Field(string name) => Fields.FirstOrDefault(f => f.Name == name);

    partial void OnExtraArgumentsChanged(string value) => Changed?.Invoke();

    private void OnFieldChanged() => Changed?.Invoke();
}
