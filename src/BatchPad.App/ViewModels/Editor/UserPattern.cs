using System.Text.RegularExpressions;

namespace BatchPad.App.ViewModels.Editor;

public static class UserPattern
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    public static bool IsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
