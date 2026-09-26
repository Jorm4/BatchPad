using System.Windows;
using System.Windows.Media;

namespace BatchPad.App.Views;

public static class VisualTree
{
    public static T? FindAncestor<T>(DependencyObject? element, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is T candidate && (match is null || match(candidate)))
                return candidate;
        }
        return null;
    }
}
