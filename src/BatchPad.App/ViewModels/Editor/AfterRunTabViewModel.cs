using System.Collections.ObjectModel;
using BatchPad.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Editor;

public sealed partial class ArtifactRowViewModel(ArtifactDefinition artifact, Action changed) : ObservableObject
{
    public static IReadOnlyList<EditorOption<ArtifactOpen>> OpenOptions { get; } =
    [
        new(ArtifactOpen.Never, "Link only"), new(ArtifactOpen.OnSuccess, "Open on success"), new(ArtifactOpen.Always, "Always open"),
    ];

    public ArtifactDefinition Artifact { get; } = artifact;

    [ObservableProperty]
    private string path = artifact.Path ?? "";

    [ObservableProperty]
    private EditorOption<ArtifactOpen> open = OpenOptions.First(o => o.Value == (artifact.Open ?? ArtifactOpen.Never));

    partial void OnPathChanged(string value)
    {
        Artifact.Path = value.Trim();
        changed();
    }

    partial void OnOpenChanged(EditorOption<ArtifactOpen> value)
    {
        Artifact.Open = value.Value == ArtifactOpen.Never ? null : value.Value;
        changed();
    }
}

/// <summary>The editor's After run tab (§5.1): ready pattern and URL, stop companion, artifacts and test report.</summary>
public sealed partial class AfterRunTabViewModel : ObservableObject
{
    private readonly ScriptNode _definition;
    private readonly Action _changed;

    /// <param name="stopChoices">Scripts that can stop this one, by reference; the first is "None".</param>
    public AfterRunTabViewModel(ScriptNode definition, IReadOnlyList<EditorOption<string?>> stopChoices, Action changed)
    {
        _definition = definition;
        _changed = changed;
        StopChoices = definition.Stop is { } current && stopChoices.All(c => c.Value != current)
            ? [.. stopChoices, new EditorOption<string?>(current, current)]
            : stopChoices;
        readyPattern = definition.Ready?.Pattern ?? "";
        openUrl = definition.Ready?.Open ?? "";
        stop = StopChoices.First(c => c.Value == definition.Stop);
        testReport = definition.TestReport ?? "";
        foreach (var artifact in definition.Artifacts ?? [])
            Artifacts.Add(new ArtifactRowViewModel(artifact, WriteArtifacts));
    }

    public IReadOnlyList<EditorOption<string?>> StopChoices { get; }
    public IReadOnlyList<EditorOption<ArtifactOpen>> OpenOptions => ArtifactRowViewModel.OpenOptions;
    public ObservableCollection<ArtifactRowViewModel> Artifacts { get; } = [];

    [ObservableProperty]
    private string readyPattern;

    /// <summary>Opened when the ready pattern matches; <c>$1</c> is the pattern's first group.</summary>
    [ObservableProperty]
    private string openUrl;

    [ObservableProperty]
    private EditorOption<string?> stop;

    [ObservableProperty]
    private string testReport;

    partial void OnReadyPatternChanged(string value) => WriteReady();
    partial void OnOpenUrlChanged(string value) => WriteReady();

    partial void OnStopChanged(EditorOption<string?> value)
    {
        _definition.Stop = value.Value;
        _changed();
    }

    partial void OnTestReportChanged(string value)
    {
        _definition.TestReport = GeneralTabViewModel.NullIfEmpty(value);
        _changed();
    }

    [RelayCommand]
    private void AddArtifact()
    {
        Artifacts.Add(new ArtifactRowViewModel(new ArtifactDefinition(), WriteArtifacts));
        WriteArtifacts();
    }

    [RelayCommand]
    private void RemoveArtifact(ArtifactRowViewModel? row)
    {
        if (row is not null && Artifacts.Remove(row))
            WriteArtifacts();
    }

    private void WriteReady()
    {
        var pattern = GeneralTabViewModel.NullIfEmpty(ReadyPattern);
        var open = GeneralTabViewModel.NullIfEmpty(OpenUrl);
        if (pattern is null && open is null)
            _definition.Ready = null;
        else
        {
            _definition.Ready ??= new ReadyDefinition();
            _definition.Ready.Pattern = pattern;
            _definition.Ready.Open = open;
        }
        _changed();
    }

    private void WriteArtifacts()
    {
        _definition.Artifacts = Artifacts.Count == 0 ? null : Artifacts.Select(a => a.Artifact).ToList();
        _changed();
    }
}
