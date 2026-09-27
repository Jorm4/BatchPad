using BatchPad.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using RunnerKind = BatchPad.Core.Model.Runner;

namespace BatchPad.App.ViewModels.Editor;

public sealed record EditorOption<T>(T Value, string Label);

public sealed partial class GeneralTabViewModel : ObservableObject
{
    private readonly ScriptNode _definition;
    private readonly string _fallbackName;
    private readonly Action _changed;
    private readonly Func<HotkeyGesture, string?> _hotkeyWarning;
    private readonly Action<bool> _recordingHotkey;

    public GeneralTabViewModel(ScriptNode definition, string fallbackName, Action changed, Func<HotkeyGesture, string?>? warnAboutHotkey = null,
        Action<bool>? recordingHotkey = null)
    {
        _hotkeyWarning = warnAboutHotkey ?? (_ => null);
        _recordingHotkey = recordingHotkey ?? (_ => { });
        _definition = definition;
        _fallbackName = fallbackName;
        name = definition.Name ?? fallbackName;
        description = definition.Description ?? "";
        runner = RunnerOptions.FirstOrDefault(o => o.Value == definition.Runner) ?? RunnerOptions[0];
        workingDir = definition.WorkingDir ?? "";
        console = ConsoleOptions.Single(o => o.Value == definition.Console);
        longRunning = definition.LongRunning == true;
        confirm = definition.Confirm ?? "";
        lockName = definition.Lock ?? "";
        machineWideLock = definition.LockScope == LockScope.Machine;
        module = definition.Module ?? "";
        hotkey = HotkeyGesture.TryParse(definition.Hotkey, out var gesture) ? gesture.ToString() : definition.Hotkey ?? "";
        hotkeyWarning = WarningFor(hotkey);
        _changed = changed;
    }

    private static readonly IReadOnlyList<EditorOption<RunnerKind?>> RunnerOptions =
    [
        new(null, "Automatic (from the file extension)"), new(RunnerKind.Batch, "Batch (cmd)"), new(RunnerKind.Python, "Python"),
        new(RunnerKind.Csharp, "C# (dotnet run)"), new(RunnerKind.Powershell, "PowerShell"), new(RunnerKind.Exe, "Program (exe)"),
        new(RunnerKind.Shell, "Shell command"),
    ];

    private static readonly IReadOnlyList<EditorOption<ConsoleMode?>> ConsoleOptions =
    [
        new(null, "Captured in BatchPad"), new(ConsoleMode.Window, "Own window"),
        new(ConsoleMode.WindowKeepOpen, "Own window, kept open"),
    ];

    public IReadOnlyList<EditorOption<RunnerKind?>> Runners => RunnerOptions;
    public IReadOnlyList<EditorOption<ConsoleMode?>> ConsoleModes => ConsoleOptions;
    public string? FilePath => _definition.Path;

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string description;

    [ObservableProperty]
    private EditorOption<RunnerKind?> runner;

    [ObservableProperty]
    private string workingDir;

    [ObservableProperty]
    private EditorOption<ConsoleMode?> console;

    [ObservableProperty]
    private bool longRunning;

    [ObservableProperty]
    private string confirm;

    [ObservableProperty]
    private string lockName;

    [ObservableProperty]
    private bool machineWideLock;

    [ObservableProperty]
    private string module;

    [ObservableProperty]
    private string hotkey;

    [ObservableProperty]
    private string? hotkeyWarning;

    /// <summary>A discovered script keeps its derived name unsaved until the user changes it.</summary>
    partial void OnNameChanged(string value) =>
        Set(() => _definition.Name = _definition.Name is null && value == _fallbackName ? null : NullIfEmpty(value));

    partial void OnDescriptionChanged(string value) => Set(() => _definition.Description = NullIfEmpty(value));
    partial void OnRunnerChanged(EditorOption<RunnerKind?> value) => Set(() => _definition.Runner = value.Value);
    partial void OnWorkingDirChanged(string value) => Set(() => _definition.WorkingDir = NullIfEmpty(value));
    partial void OnConsoleChanged(EditorOption<ConsoleMode?> value) => Set(() => _definition.Console = value.Value);
    partial void OnLongRunningChanged(bool value) => Set(() => _definition.LongRunning = value ? true : null);
    partial void OnConfirmChanged(string value) => Set(() => _definition.Confirm = NullIfEmpty(value));
    partial void OnLockNameChanged(string value) => Set(() => _definition.Lock = NullIfEmpty(value));
    partial void OnMachineWideLockChanged(bool value) => Set(() => _definition.LockScope = value ? LockScope.Machine : null);
    partial void OnModuleChanged(string value) => Set(() => _definition.Module = NullIfEmpty(value));

    partial void OnHotkeyChanged(string value)
    {
        HotkeyWarning = WarningFor(value);
        Set(() => _definition.Hotkey = NullIfEmpty(value));
    }

    public void RecordingHotkey(bool recording) => _recordingHotkey(recording);

    private string? WarningFor(string text) =>
        text.Length == 0 ? null
        : HotkeyGesture.TryParse(text, out var gesture) ? _hotkeyWarning(gesture)
        : HotkeyMessages.Invalid(text);

    private void Set(Action apply)
    {
        apply();
        _changed();
    }

    internal static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
