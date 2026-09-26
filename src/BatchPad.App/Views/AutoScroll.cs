using System.Windows;
using System.Windows.Controls;

namespace BatchPad.App.Views;

/// <summary>Keeps a list scrolled to the end while <c>Follow</c> is true; scrolling up clears it, reaching the end sets it.</summary>
public static class AutoScroll
{
    public static readonly DependencyProperty FollowProperty = DependencyProperty.RegisterAttached(
        "Follow", typeof(bool), typeof(AutoScroll),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnFollowChanged));

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(AutoScroll));

    public static bool GetFollow(DependencyObject element) => (bool)element.GetValue(FollowProperty);

    public static void SetFollow(DependencyObject element, bool value) => element.SetValue(FollowProperty, value);

    private static void OnFollowChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target || (bool)target.GetValue(HookedProperty))
            return;
        target.SetValue(HookedProperty, true);
        target.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not DependencyObject target || e.OriginalSource is not ScrollViewer viewer)
            return;
        if (e.ExtentHeightChange != 0)
        {
            if (GetFollow(target))
                viewer.ScrollToEnd();
        }
        else if (e.VerticalChange != 0)
        {
            SetFollow(target, viewer.VerticalOffset >= viewer.ScrollableHeight - 0.5);
        }
    }
}

public static class ScrollSelection
{
    public static readonly DependencyProperty IntoViewProperty = DependencyProperty.RegisterAttached(
        "IntoView", typeof(bool), typeof(ScrollSelection), new PropertyMetadata(false, OnIntoViewChanged));

    public static bool GetIntoView(DependencyObject element) => (bool)element.GetValue(IntoViewProperty);

    public static void SetIntoView(DependencyObject element, bool value) => element.SetValue(IntoViewProperty, value);

    private static void OnIntoViewChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is ListBox list && (bool)e.NewValue)
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is { } item)
                    list.ScrollIntoView(item);
            };
    }
}
