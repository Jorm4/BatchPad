using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Workflows;

namespace BatchPad.App.Views.Workflows;

public partial class WorkflowEditor : UserControl
{
    public WorkflowEditor()
    {
        InitializeComponent();
    }

    private WorkflowEditorViewModel? Editor => DataContext as WorkflowEditorViewModel;

    private void OnCardHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StepCardViewModel card })
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(StepCardViewModel), card), DragDropEffects.Move);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(StepCardViewModel)) is not null ? DragDropEffects.Move
            : e.Data.GetData(typeof(NodeViewModel)) is NodeViewModel node && Editor?.CanAddStep(node) == true ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Editor is not { } editor)
            return;
        var target = CardAt(e.OriginalSource as DependencyObject);
        var over = target?.DataContext as StepCardViewModel;
        // The middle of a card groups with it; its top and bottom edges reorder.
        var onMiddle = target is { ActualHeight: > 0 } && e.GetPosition(target).Y / target.ActualHeight is > 0.25 and < 0.75;
        var index = over is null ? (int?)null : editor.Steps.IndexOf(over.Group ?? over);
        if (e.Data.GetData(typeof(StepCardViewModel)) is StepCardViewModel card)
        {
            if (over is not null && onMiddle)
                editor.Group(card, over);
            else
                editor.MoveStep(card, index ?? editor.Steps.Count - 1);
        }
        else if (e.Data.GetData(typeof(NodeViewModel)) is NodeViewModel node)
        {
            if (over is not null && onMiddle)
                editor.GroupWith(node, over);
            else
                editor.AddStep(node, index);
        }
    }

    private static FrameworkElement? CardAt(DependencyObject? element) =>
        VisualTree.FindAncestor<Border>(element, b => b is { DataContext: StepCardViewModel, Tag: "StepCard" });
}
