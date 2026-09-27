using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BatchPad.App.Views;

/// <summary>
/// Caps a tree item's header at the tree's width, so long names trim instead of widening the tree.
/// TreeViewItem measures its header with unlimited width, which a plain TextTrimming can't overcome.
/// </summary>
public static class FitTreeWidth
{
    private const double RightGap = 8;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(FitTreeWidth), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement header || !(bool)e.NewValue)
            return;
        TreeView? tree = null;
        SizeChangedEventHandler onResize = (_, _) => Fit(header, tree);
        // The vertical scrollbar appearing narrows the viewport without resizing the tree.
        ScrollChangedEventHandler onScroll = (_, args) =>
        {
            if (args.ViewportWidthChange != 0)
                Fit(header, tree);
        };
        header.Loaded += (_, _) =>
        {
            tree = VisualTree.FindAncestor<TreeView>(header);
            if (tree is null)
                return;
            tree.SizeChanged += onResize;
            tree.AddHandler(ScrollViewer.ScrollChangedEvent, onScroll);
            Fit(header, tree);
        };
        header.Unloaded += (_, _) =>
        {
            if (tree is null)
                return;
            tree.SizeChanged -= onResize;
            tree.RemoveHandler(ScrollViewer.ScrollChangedEvent, onScroll);
        };
    }

    private static void Fit(FrameworkElement header, TreeView? tree)
    {
        if (tree is null || !tree.IsAncestorOf(header))
            return;
        var viewer = VisualTree.FindAncestor<ScrollViewer>(header, v => v.TemplatedParent == tree);
        var visible = viewer?.ViewportWidth is > 0 and var width ? width : tree.ActualWidth;
        var left = header.TransformToAncestor((Visual?)viewer ?? tree).Transform(new Point()).X;
        header.MaxWidth = Math.Max(0, visible - left - RightGap);
    }
}
