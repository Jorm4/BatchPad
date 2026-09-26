using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using BatchPad.App.Services;
using BatchPad.Core.Choices;
using BatchPad.Core.Discovery;
using Glob = BatchPad.Core.Discovery.Glob;
using BatchPad.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Editor;

public enum ChoiceSourceKind { Folder, Lines, Script, Fixed }

public sealed record SourceCard(ChoiceSourceKind Kind, string Title, string Description);

public sealed partial class PathSegmentViewModel(int index, string text) : ObservableObject
{
    public int Index { get; } = index;
    public string Text { get; } = text;

    [ObservableProperty]
    private bool isSelected;
}

public sealed partial class FileLineViewModel(int number, string text) : ObservableObject
{
    public int Number { get; } = number;
    public string Text { get; } = text;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isMatch;
}

public sealed partial class FixedChoiceRow : ObservableObject
{
    [ObservableProperty]
    private string label = "";

    [ObservableProperty]
    private string value = "";
}

/// <summary>Builds one choice source by pointing at files and lines instead of writing a pattern (§5.1).</summary>
public sealed partial class ChoiceSourcePickerViewModel : ChoicePreviewViewModel
{
    private static readonly TimeSpan ScanDelay = TimeSpan.FromMilliseconds(300);

    private readonly ChoiceEnvironment _environment;
    private readonly Debouncer _segmentScan;
    private bool _loading;
    private string? _loadedMatch;

    public ChoiceSourcePickerViewModel(ChoiceEnvironment environment, ChoiceSource? existing = null)
        : base(environment)
    {
        _environment = environment;
        _segmentScan = new Debouncer(environment.Dispatcher, ScanDelay);
        FixedRows.CollectionChanged += (_, e) =>
        {
            foreach (FixedChoiceRow row in e.NewItems ?? Array.Empty<FixedChoiceRow>())
                row.PropertyChanged += (_, _) => RefreshPreview();
            RefreshPreview();
        };
        _loading = true;
        if (existing is not null)
            Load(existing);
        _loading = false;
        RefreshPreview();
    }

    public static IReadOnlyList<SourceCard> Cards { get; } =
    [
        new(ChoiceSourceKind.Folder, "Files in a folder", "One choice per matching file, named by the file or a folder in its path."),
        new(ChoiceSourceKind.Lines, "Lines in a file", "Click a line: every word after = becomes a choice."),
        new(ChoiceSourceKind.Script, "Output of a script", "Runs a script; one choice per output line."),
        new(ChoiceSourceKind.Fixed, "Fixed list", "Type or paste a label and a value per row."),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolder), nameof(IsLines), nameof(IsScript), nameof(IsFixed))]
    private ChoiceSourceKind selectedKind = ChoiceSourceKind.Folder;

    public bool IsFolder => SelectedKind == ChoiceSourceKind.Folder;
    public bool IsLines => SelectedKind == ChoiceSourceKind.Lines;
    public bool IsScript => SelectedKind == ChoiceSourceKind.Script;
    public bool IsFixed => SelectedKind == ChoiceSourceKind.Fixed;

    [ObservableProperty]
    private string folderPath = "";

    [ObservableProperty]
    private string pattern = "*";

    public ObservableCollection<PathSegmentViewModel> Segments { get; } = [];

    [ObservableProperty]
    private bool valuesArePaths;

    [ObservableProperty]
    private string relativeTo = "";

    [ObservableProperty]
    private string filePath = "";

    public ObservableCollection<FileLineViewModel> Lines { get; } = [];

    [ObservableProperty]
    private string regexText = "";

    [ObservableProperty]
    private string splitText = "";

    [ObservableProperty]
    private bool allMatches;

    /// <summary>Part of the selected line; the pattern is derived to match it and its siblings.</summary>
    [ObservableProperty]
    private string exampleValue = "";

    [ObservableProperty]
    private string scriptPath = "";

    public ObservableCollection<FixedChoiceRow> FixedRows { get; } = [];

    [ObservableProperty]
    private bool lowercaseValues;

    public event Action<bool>? Closed;

    partial void OnSelectedKindChanged(ChoiceSourceKind value) => RefreshPreview();
    partial void OnFolderPathChanged(string value) => RefreshSegments();
    partial void OnPatternChanged(string value) => RefreshSegments();
    partial void OnFilePathChanged(string value) => LoadLines();
    partial void OnRegexTextChanged(string value) => RefreshMatches();
    partial void OnSplitTextChanged(string value) => RefreshPreview();
    partial void OnAllMatchesChanged(bool value) => RefreshMatches();
    partial void OnExampleValueChanged(string value) => DeriveFromExample();
    partial void OnScriptPathChanged(string value) => RefreshPreview();
    partial void OnLowercaseValuesChanged(bool value) => RefreshPreview();
    partial void OnValuesArePathsChanged(bool value) => RefreshPreview();
    partial void OnRelativeToChanged(string value) => RefreshPreview();

    [RelayCommand]
    private void SelectKind(ChoiceSourceKind kind) => SelectedKind = kind;

    [RelayCommand]
    private void BrowseFolder()
    {
        if (_environment.Dialogs.PickFolder(_environment.BaseDirectory) is { } folder)
            FolderPath = Relative(folder);
    }

    [RelayCommand]
    private void BrowseFile()
    {
        if (_environment.Dialogs.PickFile(_environment.BaseDirectory) is { } file)
            FilePath = Relative(file);
    }

    [RelayCommand]
    private void BrowseScript()
    {
        if (_environment.Dialogs.PickFile(_environment.BaseDirectory) is { } file)
            ScriptPath = Relative(file);
    }

    [RelayCommand]
    private void RefreshScript()
    {
        _environment.Commands?.Refresh();
        RefreshPreview();
    }

    [ObservableProperty]
    private PathSegmentViewModel? selectedSegment;

    [ObservableProperty]
    private FileLineViewModel? selectedLine;

    public void SelectSegment(PathSegmentViewModel segment) => SelectedSegment = segment;

    public void SelectLine(FileLineViewModel line) => SelectedLine = line;

    partial void OnSelectedSegmentChanged(PathSegmentViewModel? value)
    {
        foreach (var s in Segments)
            s.IsSelected = s == value;
        RefreshPreview();
    }

    /// <summary>Takes every word after the line's first <c>=</c>, or the whole line when it has none.</summary>
    partial void OnSelectedLineChanged(FileLineViewModel? value)
    {
        foreach (var l in Lines)
            l.IsSelected = l == value;
        ExampleValue = "";
        if (value is null)
            return;
        var text = value.Text.TrimStart();
        var equals = text.IndexOf('=');
        if (equals >= 0)
        {
            SplitText = " ";
            RegexText = EscapeLiteral(text[..(equals + 1)]) + """([^"\r\n]*)""";
        }
        else
        {
            SplitText = "";
            RegexText = @"^\s*(" + EscapeLiteral(text.TrimEnd()) + @")\s*$";
        }
    }

    private void DeriveFromExample()
    {
        if (SelectedLine is not { } line || ExampleValue.Trim().Length == 0)
            return;
        var start = line.Text.IndexOf(ExampleValue.Trim(), StringComparison.Ordinal);
        if (start < 0 || PatternFromExample.Derive(line.Text, start, ExampleValue.Trim().Length, Lines.Select(l => l.Text)) is not { } derived)
            return;
        SplitText = derived.Split ?? "";
        AllMatches = derived.All;
        RegexText = derived.Regex;
    }

    public FileLineViewModel? Line(int number) => Lines.FirstOrDefault(l => l.Number == number);

    [RelayCommand]
    private void AddFixedRow() => FixedRows.Add(new FixedChoiceRow());

    [RelayCommand]
    private void RemoveFixedRow(FixedChoiceRow row) => FixedRows.Remove(row);

    [RelayCommand]
    private void Accept() => Closed?.Invoke(true);

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(false);

    public ChoiceSource? BuildSource()
    {
        var transform = LowercaseValues ? "lower" : null;
        switch (SelectedKind)
        {
            case ChoiceSourceKind.Folder when FolderPath.Length > 0 || Pattern.Length > 0:
                var glob = FolderPath.Length == 0 ? Pattern : $"{FolderPath.TrimEnd('/', '\\').Replace('\\', '/')}/{Pattern}";
                var selected = Segments.FirstOrDefault(s => s.IsSelected);
                var isFileName = selected is null || selected.Index == Segments.Count - 1;
                return new ChoiceSource
                {
                    Glob = glob,
                    Stem = isFileName && !ValuesArePaths,
                    Match = isFileName || ValuesArePaths ? null : SegmentPattern(glob, selected!.Index),
                    RelativeTo = ValuesArePaths && RelativeTo.Trim().Length > 0 ? RelativeTo.Trim().Replace('\\', '/') : null,
                    ValueTransform = transform,
                };
            case ChoiceSourceKind.Lines when FilePath.Length > 0 && RegexText.Length > 0:
                return new ChoiceSource
                {
                    File = FilePath.Replace('\\', '/'),
                    Regex = RegexText,
                    Split = SplitText.Length == 0 ? null : SplitText,
                    All = AllMatches,
                    ValueTransform = transform,
                };
            case ChoiceSourceKind.Script when ScriptPath.Trim().Length > 0:
                return new ChoiceSource { Command = ScriptPath.Trim().Replace('\\', '/'), ValueTransform = transform };
            default:
                return null;
        }
    }

    public List<ChoiceDefinition> FixedChoices() =>
        FixedRows.Where(r => r.Label.Length > 0 || r.Value.Length > 0)
            .Select(r => new ChoiceDefinition
            {
                Value = r.Value.Length > 0 ? r.Value : r.Label,
                Label = r.Label.Length > 0 && r.Label != r.Value && r.Value.Length > 0 ? r.Label : null,
            })
            .ToList();

    public static string Describe(ChoiceSource source) => source switch
    {
        { Glob: { } glob } => $"Files matching {glob}" + (source.Match is { } match ? $" (named by {match})" : ""),
        { File: { } file, Regex: { } regex } => $"{(source.All ? "Every match" : "First match")} of {regex} in {file}",
        { List: { } list } => $"List \"{list}\"",
        { Command: { } command } => $"Output of {command}",
        _ => "(incomplete source)",
    } + (source.ValueTransform == "lower" ? ", values lowercased" : "");

    private void Load(ChoiceSource source)
    {
        LowercaseValues = source.ValueTransform == "lower";
        if (source.Glob is { } glob)
        {
            SelectedKind = ChoiceSourceKind.Folder;
            ValuesArePaths = !source.Stem && source.Match is null;
            RelativeTo = source.RelativeTo ?? "";
            var firstWildcard = glob.IndexOfAny(['*', '?']);
            var split = firstWildcard >= 0 ? glob.LastIndexOf('/', firstWildcard) : glob.LastIndexOf('/');
            _loadedMatch = source.Match;
            FolderPath = split > 0 ? glob[..split] : "";
            Pattern = split > 0 ? glob[(split + 1)..] : glob;
        }
        else if (source.File is { } file)
        {
            SelectedKind = ChoiceSourceKind.Lines;
            FilePath = file;
            RegexText = source.Regex ?? "";
            SplitText = source.Split ?? "";
            AllMatches = source.All;
        }
        else if (source.Command is { } command)
        {
            SelectedKind = ChoiceSourceKind.Script;
            ScriptPath = command;
        }
    }

    private void RefreshSegments()
    {
        Segments.Clear();
        SelectedSegment = null;
        if (BuildSource() is not { Glob: { } glob })
        {
            RefreshPreview();
            return;
        }
        var match = _loading ? _loadedMatch : null;
        _segmentScan.Run(() => FirstMatch(glob), example =>
        {
            var parts = example?.Split('/') ?? [];
            for (var i = 0; i < parts.Length; i++)
                Segments.Add(new PathSegmentViewModel(i, parts[i]) { IsSelected = i == parts.Length - 1 });
            if (match is not null && Segments.FirstOrDefault(s => SegmentPattern(glob, s.Index) == match) is { } segment)
                SelectSegment(segment);
            RefreshPreview();
        });
    }

    /// <summary>The first file the glob matches, relative to the base directory; only the glob's fixed prefix folder is searched.</summary>
    private string? FirstMatch(string glob)
    {
        var parts = glob.Split('/');
        var fixedCount = parts[..^1].TakeWhile(p => p.IndexOfAny(['*', '?']) < 0).Count();
        try
        {
            var root = Path.GetFullPath(Path.Combine(_environment.BaseDirectory, string.Join('/', parts[..fixedCount])));
            if (!Directory.Exists(root))
                return null;
            var remainder = string.Join('/', parts[fixedCount..]);
            var recursive = remainder.Contains('/') || remainder.Contains("**");
            return Directory.EnumerateFiles(root, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => Glob.IsMatch(remainder, Path.GetRelativePath(root, f).Replace('\\', '/')))
                .Select(Relative)
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void LoadLines()
    {
        Lines.Clear();
        SelectedLine = null;
        var path = Path.Combine(_environment.BaseDirectory, FilePath);
        if (FilePath.Length > 0 && File.Exists(path))
        {
            var number = 0;
            foreach (var text in File.ReadLines(path).Take(2000))
                Lines.Add(new FileLineViewModel(++number, text));
        }
        RefreshMatches();
    }

    private void RefreshMatches()
    {
        Regex? regex = null;
        try
        {
            regex = RegexText.Length == 0 ? null : new Regex(RegexText, RegexOptions.CultureInvariant, UserPattern.Timeout);
        }
        catch (ArgumentException)
        {
        }
        var first = true;
        foreach (var line in Lines)
        {
            line.IsMatch = regex is not null && UserPattern.IsMatch(regex, line.Text) && (AllMatches || first);
            first &= !line.IsMatch;
        }
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (_loading)
            return;
        var parameter = new ParameterDefinition { Type = ParameterType.Choice };
        if (SelectedKind == ChoiceSourceKind.Fixed)
            parameter.Choices = FixedChoices();
        else if (BuildSource() is { } source)
            parameter.ChoicesFrom = [source];
        ShowPreview(parameter);
    }

    /// <summary>A regex over the file's workspace-relative path whose group 1 is path segment <paramref name="index"/>.</summary>
    private static string SegmentPattern(string glob, int index)
    {
        var parts = glob.Replace('\\', '/').Split('/');
        var prefix = parts.Take(index).Select(p => p.IndexOfAny(['*', '?']) >= 0 ? "[^/]+" : EscapeLiteral(p));
        return "^" + string.Concat(prefix.Select(p => p + "/")) + "([^/]+)/";
    }

    private static string EscapeLiteral(string text) => Regex.Escape(text).Replace("\\ ", " ").Replace("\\#", "#");

    private string Relative(string path) => Path.GetRelativePath(_environment.BaseDirectory, path).Replace('\\', '/');
}
