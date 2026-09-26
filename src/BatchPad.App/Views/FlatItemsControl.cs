using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace BatchPad.App.Views;

/// <summary>
/// An ItemsControl whose automation tree is its visual tree. The default data-item peers hide most templated
/// controls from UI Automation, so UI tests and screen readers could not reach the form fields.
/// </summary>
public sealed class FlatItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
