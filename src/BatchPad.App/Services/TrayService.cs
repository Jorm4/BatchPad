using System.Drawing;
using Forms = System.Windows.Forms;

namespace BatchPad.App.Services;

public sealed class TrayService : ITrayService
{
    private Forms.NotifyIcon? _icon;
    private Icon? _image;
    private Action? _notificationClick;

    public bool IsVisible
    {
        get => _icon?.Visible == true;
        set
        {
            if (value || _icon is not null)
                TrayIcon.Visible = value;
        }
    }

    public event Action? OpenRequested;
    public event Action? SchedulesRequested;
    public event Action? ExitRequested;

    public void Notify(string title, string message, Action onClick)
    {
        _notificationClick = onClick;
        TrayIcon.Visible = true;
        TrayIcon.ShowBalloonTip(10_000, title, string.IsNullOrWhiteSpace(message) ? title : message, Forms.ToolTipIcon.Error);
    }

    private Forms.NotifyIcon TrayIcon => _icon ??= CreateIcon();

    private Forms.NotifyIcon CreateIcon()
    {
        _image = Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } own
            ? own
            : (Icon)SystemIcons.Application.Clone();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Schedules", null, (_, _) => SchedulesRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        var icon = new Forms.NotifyIcon { Text = "BatchPad", Icon = _image, ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        icon.BalloonTipClicked += (_, _) => _notificationClick?.Invoke();
        return icon;
    }

    public void Dispose()
    {
        if (_icon is null)
            return;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _image?.Dispose();
    }
}
