using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.App.Services;
using BatchPad.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Parameters;

public abstract partial class ParameterFieldViewModel : ObservableObject
{
    private bool _loaded;

    protected ParameterFieldViewModel(ParameterDefinition definition, bool isSet)
    {
        Definition = definition;
        IsSet = isSet;
    }

    public ParameterDefinition Definition { get; }
    public string Name => Definition.Name!;
    public string Label => Definition.Label ?? Definition.Name!;
    public string? Description => Definition.Description;
    public string AutomationId => $"Param_{Name}";

    /// <summary>
    /// False until the user (or the session) sets a value. An unset field sends nothing, so the assembler applies
    /// the definition's default and expands its variables.
    /// </summary>
    public bool IsSet { get; private set; }

    public abstract JsonNode? Value { get; }

    [ObservableProperty]
    private string? error;

    public event Action? Changed;

    protected void EndLoad() => _loaded = true;

    protected void OnEdited()
    {
        if (!_loaded)
            return;
        IsSet = true;
        Changed?.Invoke();
    }

    protected static string? TextOf(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => node.ToJsonString(),
    };
}

public sealed partial class FlagFieldViewModel : ParameterFieldViewModel
{
    public FlagFieldViewModel(ParameterDefinition definition, JsonNode? stored, bool isSet) : base(definition, isSet)
    {
        IsChecked = string.Equals(TextOf(stored ?? definition.Default), "true", StringComparison.OrdinalIgnoreCase);
        EndLoad();
    }

    [ObservableProperty]
    private bool isChecked;

    public override JsonNode? Value => JsonValue.Create(IsChecked);

    partial void OnIsCheckedChanged(bool value) => OnEdited();
}

public partial class TextFieldViewModel : ParameterFieldViewModel
{
    public TextFieldViewModel(ParameterDefinition definition, JsonNode? stored, bool isSet) : base(definition, isSet)
    {
        Text = TextOf(stored ?? definition.Default) ?? "";
        Validate();
        EndLoad();
    }

    [ObservableProperty]
    private string text = "";

    public override JsonNode? Value => Text.Length == 0 ? null : JsonValue.Create(Text);

    partial void OnTextChanged(string value)
    {
        Validate();
        OnEdited();
    }

    protected virtual void Validate()
    {
    }
}

/// <summary>A <c>secret</c> value: shown as a password box, never saved with values or history, only in Credential Manager when remembered.</summary>
public sealed class SecretFieldViewModel(ParameterDefinition definition, JsonNode? stored, bool isSet)
    : TextFieldViewModel(definition, stored, isSet)
{
    public bool CanRemember { get; set; }

    public bool Remember { get; set; }
}

public sealed class IntFieldViewModel(ParameterDefinition definition, JsonNode? stored, bool isSet)
    : TextFieldViewModel(definition, stored, isSet)
{
    public override JsonNode? Value =>
        int.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : null;

    protected override void Validate()
    {
        var (min, max) = (Definition.Min, Definition.Max);
        if (Text.Trim().Length == 0)
            Error = null;
        else if (!int.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            Error = "Enter a whole number.";
        else if (number < (min ?? int.MinValue) || number > (max ?? int.MaxValue))
            Error = (min, max) switch
            {
                ({ } low, { } high) => $"Must be between {low} and {high}.",
                ({ } low, null) => $"Must be at least {low}.",
                _ => $"Must be at most {max}.",
            };
        else
            Error = null;
    }
}

public sealed partial class PathFieldViewModel(
    ParameterDefinition definition, JsonNode? stored, bool isSet, IFileDialogService dialogs, string baseDirectory)
    : TextFieldViewModel(definition, stored, isSet)
{
    public bool IsFolder => Definition.Mode == "folder";

    [RelayCommand]
    private void Browse()
    {
        var start = Text.Length > 0 && Directory.Exists(Path.Combine(baseDirectory, Text)) ? Path.Combine(baseDirectory, Text) : baseDirectory;
        if ((IsFolder ? dialogs.PickFolder(start) : dialogs.PickFile(start)) is { } picked)
            Text = picked;
    }
}
