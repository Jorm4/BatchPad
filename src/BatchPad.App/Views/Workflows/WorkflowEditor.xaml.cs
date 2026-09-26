using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        var over = CardAt(e.OriginalSource as DependencyObject);
        var index = over is null ? (int?)null : editor.Steps.IndexOf(over);
        if (e.Data.GetData(typeof(StepCardViewModel)) is StepCardViewModel card)
            editor.MoveStep(card, index ?? editor.Steps.Count - 1);
        else if (e.Data.GetData(typeof(NodeViewModel)) is NodeViewModel node)
            editor.AddStep(node, index);
    }

    private static StepCardViewModel? CardAt(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is FrameworkElement { DataContext: StepCardViewModel card })
                return card;
        return null;
    }
}
