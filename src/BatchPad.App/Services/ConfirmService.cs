using System.Windows;

namespace BatchPad.App.Services;

public interface IConfirmService
{
    bool Confirm(string title, string message);

    /// <returns>True for Yes, false for No, null for Cancel.</returns>
    bool? ConfirmOrCancel(string title, string message);
}

public sealed class MessageBoxConfirmService : IConfirmService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool? ConfirmOrCancel(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };
}
