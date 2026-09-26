using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatchPad.App.ViewModels;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private Point _dragStart;
    private NodeViewModel? _dragCandidate;

    private MainViewModel? Main => DataContext as MainViewModel;

    private void OnSearchClick(object sender, RoutedEventArgs e) => FilterBox.Focus();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (Main?.WindowLayout is not { Width: > 0, Height: > 0 } layout)
            return;
        var visible = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (visible.IntersectsWith(new Rect(layout.Left, layout.Top, layout.Width, layout.Height)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = layout.Left;
            Top = layout.Top;
        }
        Width = layout.Width;
        Height = layout.Height;
        if (layout.TreeWidth > 0)
            TreeColumn.Width = new GridLength(layout.TreeWidth);
        if (layout.OutputHeight > 0)
            OutputRow.Height = new GridLength(layout.OutputHeight);
        if (layout.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var bounds = RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        Main?.SaveWindowLayout(new WindowLayout
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
            TreeWidth = TreeColumn.ActualWidth,
            OutputHeight = OutputRow.ActualHeight,
        });
    }

    /// <summary>Selects the item under the mouse so the context menu acts on it.</summary>
    private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is TreeViewItem item)
            {
                item.IsSelected = true;
                return;
            }
        }
    }

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = e.OriginalSource is DependencyObject source && FindAncestor<TextBox>(source) is null ? NodeAt(source) : null;
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not { Kind: not NodeKind.Root } node)
            return;
        var offset = e.GetPosition(null) - _dragStart;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragCandidate = null;
        DragDrop.DoDragDrop(ScriptTreeView, new DataObject(typeof(NodeViewModel), node), DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropFor(e) is ({ } dragged, _) ? dragged.IsMyScript ? DragDropEffects.Move : DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        if (DropFor(e) is ({ } dragged, { } target))
            Main?.MyScripts.DragDrop.Drop(dragged, target);
        e.Handled = true;
    }

    private static (NodeViewModel?, NodeViewModel?) DropFor(DragEventArgs e) =>
        e.Data.GetData(typeof(NodeViewModel)) is NodeViewModel dragged && e.OriginalSource is DependencyObject source
        && NodeAt(source) is { } target && DragDropHandler.CanDrop(dragged, target)
            ? (dragged, target)
            : (null, null);

    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            });
    }

    private static NodeViewModel? NodeAt(DependencyObject source) => FindAncestor<TreeViewItem>(source)?.DataContext as NodeViewModel;

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is T match)
                return match;
        return null;
    }
}
