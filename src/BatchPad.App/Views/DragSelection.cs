using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BatchPad.App.Views;

/// <summary>
/// Selects the lines between where a drag started and the mouse, scrolling while it is above or below the list; a click, a
/// modified click and a link keep their usual behaviour.
/// </summary>
public sealed class DragSelection
{
    private readonly ListBox _list;
    private readonly DispatcherTimer _edgeScroll;
    private int? _anchor;
    private Point _start;
    private Point _mouse;
    private (int From, int To)? _range;
    private bool _capturing;
    private IInputElement? _captured;

    private DragSelection(ListBox list)
    {
        _list = list;
        _edgeScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Input, (_, _) => ScrollAtEdge(), list.Dispatcher);
        _edgeScroll.Stop();
    }

    public static DragSelection Attach(ListBox list)
    {
        var drag = new DragSelection(list);
        list.PreviewMouseLeftButtonDown += drag.OnDown;
        list.PreviewMouseMove += drag.OnMove;
        list.PreviewMouseLeftButtonUp += drag.OnUp;
        list.LostMouseCapture += (_, _) =>
        {
            if (!drag._capturing)
                drag.End();
        };
        return drag;
    }

    /// <summary>The indexes to deselect and to select when the selected range goes from <paramref name="old"/> to <paramref name="now"/>.</summary>
    public static (IEnumerable<int> Remove, IEnumerable<int> Add) Delta((int From, int To)? old, (int From, int To) now)
    {
        var (from, to) = old ?? (0, -1);
        return (Enumerable.Range(from, to - from + 1).Where(i => i < now.From || i > now.To),
            Enumerable.Range(now.From, now.To - now.From + 1).Where(i => i < from || i > to));
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None)
            Press(e.GetPosition(_list));
        else
            End();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && Drag(e.GetPosition(_list)))
            e.Handled = true;
    }

    private void OnUp(object sender, MouseButtonEventArgs e) => e.Handled = Release();

    /// <summary>The button went down at <paramref name="point"/>, relative to the list.</summary>
    public void Press(Point point)
    {
        End();
        _start = point;
        _anchor = IndexAt(point);
    }

    /// <returns>True once the move has become a drag selection.</returns>
    public bool Drag(Point point)
    {
        if (_anchor is not { } anchor)
            return false;
        _mouse = point;
        if (_range is null)
        {
            if (Math.Abs(point.Y - _start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return false;
            // A window that isn't active gives the capture straight back; that must not end the drag being started.
            _capturing = true;
            try
            {
                // Not the list itself: while the list holds the mouse, each line the mouse enters makes the list select just
                // that line, undoing the range.
                _captured = Viewer() ?? (IInputElement)_list;
                Mouse.Capture(_captured, CaptureMode.Element);
            }
            finally
            {
                _capturing = false;
            }
            _edgeScroll.Start();
            // The list's own click has already selected the line the drag started on.
            if (_list.SelectedItems.Contains(_list.Items[anchor]))
                _range = (anchor, anchor);
        }
        SelectToMouse();
        return true;
    }

    /// <returns>True when a drag selection ended.</returns>
    public bool Release()
    {
        var dragged = _range is not null;
        End();
        return dragged;
    }

    private void End()
    {
        _anchor = null;
        _range = null;
        _edgeScroll.Stop();
        if (_captured is not null && Mouse.Captured == _captured)
            Mouse.Capture(null);
        _captured = null;
    }

    private void ScrollAtEdge()
    {
        if (_range is null || Viewer() is not { } viewer)
            return;
        if (_mouse.Y < 0)
            viewer.LineUp();
        else if (_mouse.Y > _list.ActualHeight)
            viewer.LineDown();
        else
            return;
        _list.UpdateLayout();
        SelectToMouse();
    }

    private void SelectToMouse()
    {
        if (_anchor is not { } anchor || IndexAt(_mouse) is not { } index)
            return;
        var now = (Math.Min(anchor, index), Math.Max(anchor, index));
        // Anything else that changed the selection mid-drag is overruled by rebuilding the whole range.
        if (_range is { } range && _list.SelectedItems.Count != range.To - range.From + 1)
        {
            _list.UnselectAll();
            _range = null;
        }
        var (remove, add) = Delta(_range, now);
        foreach (var i in remove.ToList())
            _list.SelectedItems.Remove(_list.Items[i]);
        foreach (var i in add.ToList())
            _list.SelectedItems.Add(_list.Items[i]);
        _range = now;
    }

    /// <summary>The line at <paramref name="point"/>, held inside the list so a mouse above or below it picks the edge line.</summary>
    private int? IndexAt(Point point)
    {
        var inside = new Point(Math.Clamp(point.X, 2, Math.Max(2, _list.ActualWidth - 24)), Math.Clamp(point.Y, 2, Math.Max(2, _list.ActualHeight - 2)));
        return VisualTree.FindAncestor<ListBoxItem>(_list.InputHitTest(inside) as DependencyObject) is { } item
            && _list.ItemContainerGenerator.IndexFromContainer(item) is >= 0 and var index
            ? index
            : null;
    }

    private ScrollViewer? Viewer() => FindChild<ScrollViewer>(_list);

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found || (found = FindChild<T>(child)!) is not null)
                return found;
        }
        return null;
    }
}
