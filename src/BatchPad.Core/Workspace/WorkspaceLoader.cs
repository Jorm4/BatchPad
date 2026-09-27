using System.Security.Cryptography;
using System.Text;
using BatchPad.Core.Config;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Trust;
using BatchPad.Core.Workflows;

namespace BatchPad.Core.Workspace;

public sealed class LoadedWorkspace
{
    public required string FilePath { get; init; }
    public required string Id { get; init; }
    public required ScriptTree MyScripts { get; init; }
    public required ScriptTree Workspace { get; init; }
    public required ScriptTree Global { get; init; }
    public required ReferenceResolver References { get; init; }
    public required IReadOnlyList<LoadError> Errors { get; init; }

    public string Directory => Workspace.BaseDirectory;

    /// <summary>What locks are scoped to by default.</summary>
    public string CheckoutDirectory => _checkoutDirectory ??= Checkout.DirectoryOf(Directory);

    private string? _checkoutDirectory;
    public IEnumerable<ScriptTree> Trees => [MyScripts, Workspace, Global];

    public IEnumerable<ScriptTree> AllTrees => Trees.SelectMany(t => t.SelfAndParts());

    public IEnumerable<string> ConfigDirectories => AllTrees.Select(t => t.BaseDirectory).Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> ScriptDirectories =>
        AllTrees.SelectMany(t => t.ScriptFolderDirectories).Distinct(StringComparer.OrdinalIgnoreCase);
}

public static class WorkspaceLoader
{
    /// <summary>Loads all three trees. Problems are collected in <see cref="LoadedWorkspace.Errors"/>; a file that fails to load becomes an empty tree.</summary>
    /// <param name="trust">Decides whether the workspace's files may reach outside its folder; without it they may not.</param>
    public static LoadedWorkspace Load(string workspaceFile, AppPaths paths, TrustStore? trust = null)
    {
        var errors = new List<LoadError>();
        var fullPath = Path.GetFullPath(workspaceFile);
        var directory = Path.GetDirectoryName(fullPath)!;
        var workspace = new ScriptTree(TreeKind.Workspace, fullPath, ReadOrEmpty(fullPath, errors, reportMissing: true))
        {
            Paths = PathPolicy.Workspace(directory, trust?.IsTrusted(directory) == true),
        };
        var id = ComputeId(workspace.File, fullPath);
        var global = new ScriptTree(TreeKind.Global, paths.GlobalFile, ReadOrEmpty(paths.GlobalFile, errors));
        var userFile = paths.UserFile(id);
        var myScripts = new ScriptTree(TreeKind.MyScripts, userFile, ReadOrEmpty(userFile, errors));
        ScriptTree[] trees = [myScripts, workspace, global];
        foreach (var tree in trees)
            AddIncludes(tree, errors, new(StringComparer.OrdinalIgnoreCase) { tree.FilePath });
        AddLibraries(global, errors);
        var references = ReferenceResolver.Build(trees, errors);
        var allTrees = trees.SelectMany(t => t.SelfAndParts()).ToList();
        foreach (var tree in allTrees)
        {
            CheckSchedules(tree, errors);
            CheckScriptFolders(tree, errors);
        }
        errors.AddRange(WorkflowValidator.FindCycles(allTrees, references));
        errors.AddRange(Prerequisites.FindCycles(allTrees, references));
        CheckHotkeys(allTrees, errors);
        workspace.Rescan(myScripts.File.SeenPaths?.ToHashSet(StringComparer.OrdinalIgnoreCase));
        global.Rescan();
        myScripts.Rescan();

        return new LoadedWorkspace
        {
            FilePath = fullPath,
            Id = id,
            MyScripts = myScripts,
            Workspace = workspace,
            Global = global,
            References = references,
            Errors = errors,
        };
    }

    private static void AddIncludes(ScriptTree tree, List<LoadError> errors, HashSet<string> loaded)
    {
        foreach (var include in tree.File.Include ?? [])
        {
            string path;
            try
            {
                path = tree.Paths.Resolve(include, tree.BaseDirectory);
            }
            catch (Exception ex) when (ex is UnsafePathException or ArgumentException)
            {
                errors.Add(new LoadError($"Include '{include}': {ex.Message}", tree.FilePath));
                continue;
            }
            if (!loaded.Add(path))
            {
                errors.Add(new LoadError($"'{include}' is included more than once.", tree.FilePath));
                continue;
            }
            var part = new ScriptTree(tree.Kind, path, ReadOrEmpty(path, errors, reportMissing: true)) { Library = tree.Library, IsPart = true, Paths = tree.Paths };
            tree.Parts.Add(part);
            AddIncludes(part, errors, loaded);
        }
    }

    /// <summary>A library is named after its file: <c>team.json</c> gives <c>global:team:id</c>.</summary>
    private static void AddLibraries(ScriptTree global, List<LoadError> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in global.File.Libraries ?? [])
        {
            var path = Path.GetFullPath(library, global.BaseDirectory);
            var name = Path.GetFileNameWithoutExtension(path);
            if (!names.Add(name))
            {
                errors.Add(new LoadError($"Library '{name}' is listed more than once.", global.FilePath));
                continue;
            }
            var part = new ScriptTree(TreeKind.Global, path, ReadOrEmpty(path, errors, reportMissing: true)) { Library = name, IsPart = true };
            global.Parts.Add(part);
            AddIncludes(part, errors, new(StringComparer.OrdinalIgnoreCase) { path });
        }
    }

    /// <summary>Only the personal files' own schedules count; shared files (and included parts) keep theirs unused.</summary>
    private static void CheckSchedules(ScriptTree tree, List<LoadError> errors)
    {
        if (tree.File.Schedules is not { } schedules)
            return;
        if (tree.IsPart || tree.Kind == TreeKind.Workspace)
        {
            errors.Add(new LoadError("Schedules are personal and are ignored here: they belong in user.json or global.json.", tree.FilePath));
            return;
        }
        foreach (var schedule in schedules)
        {
            if (TriggerMath.Problem(schedule.Trigger) is { } problem)
                errors.Add(new LoadError($"Schedule '{schedule.Key}': {problem}", tree.FilePath));
        }
    }

    private static void CheckScriptFolders(ScriptTree tree, List<LoadError> errors)
    {
        foreach (var folder in tree.DeclaredScriptFolders)
        {
            if (tree.Paths.Problem(ScriptFolderScanner.FullPath(tree.BaseDirectory, folder)) is { } problem)
                errors.Add(new LoadError($"Script folder '{folder.Path}' is skipped: {problem}", tree.FilePath));
        }
    }

    private static void CheckHotkeys(IEnumerable<ScriptTree> trees, List<LoadError> errors)
    {
        var owners = new Dictionary<HotkeyGesture, string>();
        foreach (var tree in trees)
            foreach (var (node, location) in tree.AllNodes())
            {
                if (node is not RunnableNode { Hotkey: { } text })
                    continue;
                if (!HotkeyGesture.TryParse(text, out var gesture))
                    errors.Add(new LoadError($"'{location}': {HotkeyMessages.Invalid(text)}", tree.FilePath));
                else if (!owners.TryAdd(gesture, location))
                    errors.Add(new LoadError($"'{location}': {HotkeyMessages.Duplicate(gesture, owners[gesture])}", tree.FilePath));
            }
    }

    /// <summary>The file's <c>id</c>, or a hash of its full path (§3.7). The id names a folder, so an unsafe one is hashed too.</summary>
    public static string ComputeId(WorkspaceFile file, string fullPath)
    {
        if (file.Id is { Length: > 0 } id && IsSafeFolderName(id))
            return id;
        var key = file.Id is { Length: > 0 } ? file.Id : fullPath.ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    private static bool IsSafeFolderName(string id) =>
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.EndsWith('.') && !id.EndsWith(' ') && !PathPolicy.IsDeviceName(id);

    private static WorkspaceFile ReadOrEmpty(string path, List<LoadError> errors, bool reportMissing = false)
    {
        if (!File.Exists(path))
        {
            if (reportMissing)
                errors.Add(new LoadError("File not found.", path));
            return new WorkspaceFile();
        }
        try
        {
            return ConfigReader.ReadFile(path);
        }
        catch (ConfigException ex)
        {
            errors.Add(new LoadError(ex.Message, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new LoadError(ex.Message, path));
        }
        return new WorkspaceFile();
    }
}
