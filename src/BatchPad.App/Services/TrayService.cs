using System.Drawing;
using Forms = System.Windows.Forms;

namespace BatchPad.App.Services;

public sealed class TrayService : ITrayService
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _image;
    private Action? _notificationClick;

    public TrayService()
    {
        _image = Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } own ? own : (Icon)SystemIcons.Application.Clone();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Schedules", null, (_, _) => SchedulesRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon = new Forms.NotifyIcon { Text = "BatchPad", Icon = _image, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => _notificationClick?.Invoke();
    }

    public bool IsVisible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public event Action? OpenRequested;
    public event Action? SchedulesRequested;
    public event Action? ExitRequested;

    public void Notify(string title, string message, Action onClick)
    {
        _notificationClick = onClick;
        _icon.Visible = true;
        _icon.ShowBalloonTip(10_000, title, message, Forms.ToolTipIcon.Error);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _image.Dispose();
    }
}
