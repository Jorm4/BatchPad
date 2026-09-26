using System.Windows;
using System.Windows.Controls;

namespace BatchPad.App.Views;

/// <summary>Makes <see cref="PasswordBox.Password"/> bindable, which WPF leaves out on purpose.</summary>
public static class PasswordBinding
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(PasswordBinding),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    public static string GetPassword(DependencyObject target) => (string)target.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject target, string value) => target.SetValue(PasswordProperty, value);

    private static void OnPasswordChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not PasswordBox box)
            return;
        box.PasswordChanged -= OnBoxChanged;
        if (box.Password != (string?)e.NewValue)
            box.Password = (string?)e.NewValue ?? "";
        box.PasswordChanged += OnBoxChanged;
    }

    private static void OnBoxChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        SetPassword(box, box.Password);
    }
}
