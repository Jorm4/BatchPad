namespace BatchPad.Core.Running;

/// <summary>Opens a URL or file with its default handler; faked in tests so nothing launches a browser.</summary>
public interface IShellOpener
{
    void Open(string target);
}
