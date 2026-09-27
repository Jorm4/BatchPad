using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _closeConfirmed;
    private bool _exiting;

    private MainViewModel? Main => DataContext as MainViewModel;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Main?.Palette.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (Main is { } main)
        {
            main.ShowWindowRequested += Restore;
            main.ExitRequested += () =>
            {
                _exiting = true;
                Close();
            };
        }
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

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeConfirmed && Main is { } main)
        {
            var exiting = _exiting;
            _exiting = false;
            switch (main.ConfirmClose(exiting))
            {
                case CloseAction.Cancel:
                    e.Cancel = true;
                    return;
                case CloseAction.HideToTray:
                    e.Cancel = true;
                    Hide();
                    return;
                case CloseAction.StopThenClose:
                    e.Cancel = true;
                    IsEnabled = false;
                    try
                    {
                        await main.StopAllAsync();
                    }
                    finally
                    {
                        _closeConfirmed = true;
                        Close();
                    }
                    return;
            }
        }
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

    private void Restore()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    /// <summary>Selects the item under the mouse so the context menu acts on it.</summary>
    private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
    {
        if (VisualTree.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
            item.IsSelected = true;
    }

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = e.OriginalSource is DependencyObject source && VisualTree.FindAncestor<TextBox>(source) is null ? NodeAt(source) : null;
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
        e.Effects = DropFor(e) is ({ } dragged, _) ? dragged.IsMyScript ? DragDropEffects.Move : DragDropEffects.Copy
            : HasExternalItems(e) && ExternalTarget(e) is not null ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        if (DropFor(e) is ({ } dragged, { } target))
            Main?.MyScripts.DragDrop.Drop(dragged, target);
        else if (ExternalItems(e) is { Count: > 0 } items && ExternalTarget(e) is { } externalTarget)
            Main?.MyScripts.DragDrop.DropExternal(items, externalTarget);
        e.Handled = true;
    }

    /// <summary>Files and folders from Explorer, or a URL from a browser.</summary>
    private static IReadOnlyList<string> ExternalItems(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            return files;
        return WebUrl(e) is { } url ? [url] : [];
    }

    private static bool HasExternalItems(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop) || WebUrl(e) is not null;

    private static string? WebUrl(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.UnicodeText) && e.Data.GetData(DataFormats.UnicodeText) is string text
        && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? text.Trim()
            : null;

    private static NodeViewModel? ExternalTarget(DragEventArgs e) =>
        e.OriginalSource is DependencyObject source ? NodeAt(source) : null;

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

    private static NodeViewModel? NodeAt(DependencyObject source) => VisualTree.FindAncestor<TreeViewItem>(source)?.DataContext as NodeViewModel;
}
