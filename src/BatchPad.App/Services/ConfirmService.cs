using System.Windows;

namespace BatchPad.App.Services;

public interface IConfirmService
{
    bool Confirm(string title, string message);
}

public sealed class MessageBoxConfirmService : IConfirmService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
