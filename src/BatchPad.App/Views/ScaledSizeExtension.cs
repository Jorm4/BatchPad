using System.Windows.Markup;
using Microsoft.Win32;

namespace BatchPad.App.Views;

/// <summary>
/// A font size scaled by Windows' "Text size" setting, as the theme scales its own text; a plain FontSize="20" is not, and ends
/// up smaller than body text at a larger setting.
/// </summary>
[MarkupExtensionReturnType(typeof(double))]
public sealed class ScaledSizeExtension(double size) : MarkupExtension
{
    public static double Factor { get; } = ReadFactor();

    public override object ProvideValue(IServiceProvider serviceProvider) => size * Factor;

    private static double ReadFactor()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor", 100) is int percent
                && percent is >= 100 and <= 225 ? percent / 100.0 : 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }
}
