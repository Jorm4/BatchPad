using BatchPad.Core.Workspace;

namespace BatchPad.App.ViewModels;

/// <summary>Tree drag-and-drop rules (§5), kept out of the view so they can be tested without a mouse.</summary>
public sealed class DragDropHandler(MyScriptsViewModel myScripts)
{
    /// <summary>Shared scripts drop onto My Scripts as customisations; My Scripts entries move within it, never into themselves.</summary>
    public static bool CanDrop(NodeViewModel dragged, NodeViewModel target)
    {
        if (target.Tree.Kind != TreeKind.MyScripts)
            return false;
        if (dragged.Tree.Kind != TreeKind.MyScripts)
            return MyScriptsViewModel.CanAdd(dragged);
        return dragged.IsMyScript && !target.IsWithin(dragged);
    }

    public bool Drop(NodeViewModel dragged, NodeViewModel target) =>
        CanDrop(dragged, target)
        && (dragged.Tree.Kind == TreeKind.MyScripts ? myScripts.Move(dragged, target) : myScripts.Add(dragged, target));
}
