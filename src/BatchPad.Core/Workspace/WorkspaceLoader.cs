using System.Security.Cryptography;
using System.Text;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
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
    public IEnumerable<ScriptTree> Trees => [MyScripts, Workspace, Global];
}

public static class WorkspaceLoader
{
    /// <summary>Loads all three trees. Problems are collected in <see cref="LoadedWorkspace.Errors"/>; a file that fails to load becomes an empty tree.</summary>
    public static LoadedWorkspace Load(string workspaceFile, AppPaths paths)
    {
        var errors = new List<LoadError>();
        var fullPath = Path.GetFullPath(workspaceFile);
        var workspace = new ScriptTree(TreeKind.Workspace, fullPath, ReadOrEmpty(fullPath, errors, reportMissing: true));
        var id = ComputeId(workspace.File, fullPath);
        var global = new ScriptTree(TreeKind.Global, paths.GlobalFile, ReadOrEmpty(paths.GlobalFile, errors));
        var userFile = paths.UserFile(id);
        var myScripts = new ScriptTree(TreeKind.MyScripts, userFile, ReadOrEmpty(userFile, errors));
        var references = ReferenceResolver.Build([myScripts, workspace, global], errors);
        errors.AddRange(WorkflowValidator.FindCycles([myScripts, workspace, global], references));
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

    /// <summary>The file's <c>id</c>, or a hash of its full path (§3.7). The id names a folder, so an unsafe one is hashed too.</summary>
    public static string ComputeId(WorkspaceFile file, string fullPath)
    {
        if (file.Id is { Length: > 0 } id && IsSafeFolderName(id))
            return id;
        var key = file.Id is { Length: > 0 } ? file.Id : fullPath.ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    private static bool IsSafeFolderName(string id) =>
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && id.Trim('.').Length > 0;

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
        catch (IOException ex)
        {
            errors.Add(new LoadError(ex.Message, path));
        }
        return new WorkspaceFile();
    }
}
