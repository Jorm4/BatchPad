using Microsoft.Win32;

namespace BatchPad.App.Services;

public interface IFileDialogService
{
    string? PickFile(string initialDirectory);
    string? PickFolder(string initialDirectory);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? PickFile(string initialDirectory)
    {
        var dialog = new OpenFileDialog { InitialDirectory = initialDirectory };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string initialDirectory)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = initialDirectory };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
