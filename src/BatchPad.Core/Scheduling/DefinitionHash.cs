using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Templating;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Scheduling;

/// <summary>
/// Fingerprints what a schedule runs (§4.2): the target reference, its definition, every script or workflow it reaches
/// through workflow steps and <c>dependsOn</c>, those scripts' file contents, and the workspace's shared values, so a
/// pulled change to any of them pauses the schedule.
/// </summary>
public static class DefinitionHash
{
    public static string Of(ScheduleTarget target)
    {
        var text = new StringBuilder(target.Reference).Append('\n');
        var shared = target.Workspace.Workspace.File;
        text.Append(JsonSerializer.Serialize(new { shared.Variables, shared.SharedParams, shared.Lists }, ConfigJson.Options)).Append('\n');
        var visited = new HashSet<TreeNode>(ReferenceEqualityComparer.Instance);
        var references = target.Workspace.References;
        Append(target.Definition, target.Tree);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];

        void Append(RunnableNode node, ScriptTree tree)
        {
            if (!visited.Add(node))
                return;
            text.Append(JsonSerializer.Serialize<TreeNode>(node, ConfigJson.Options)).Append('\n');
            if (node is ScriptNode { Path: { } path })
                text.Append(FileHash(tree.BaseDirectory, path)).Append('\n');
            IEnumerable<string?> reached = node switch
            {
                WorkflowNode workflow => workflow.Steps.SelectMany(s => s.Leaves()).Select(s => s.Run),
                ScriptNode script => script.DependsOn ?? [],
                _ => [],
            };
            foreach (var reference in reached.OfType<string>())
            {
                if (references.Resolve(reference, tree) is RunnableNode next && references.TreeOf(next) is { } nextTree)
                    Append(next, nextTree);
                else
                    text.Append("missing ").Append(reference).Append('\n');
            }
        }
    }

    private static string FileHash(string baseDirectory, string path)
    {
        if (TemplateExpander.HasVariables(path))
            return "templated";
        try
        {
            var full = Path.GetFullPath(Path.Combine(baseDirectory, path));
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return "remote";
            using var stream = File.OpenRead(full);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "unreadable";
        }
    }
}
