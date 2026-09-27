using System.Runtime.InteropServices;
using System.Windows.Interop;
using BatchPad.Core.Model;

namespace BatchPad.App.Services;

public sealed class Win32HotkeyApi : IHotkeyApi
{
    public const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly HwndSource _source;

    public Win32HotkeyApi(HwndSource source)
    {
        _source = source;
        source.AddHook(Hook);
    }

    public event Action<int>? Pressed;

    public bool Register(int id, HotkeyGesture gesture) =>
        RegisterHotKey(_source.Handle, id, (uint)gesture.Modifiers | ModNoRepeat, (uint)gesture.VirtualKey);

    public void Unregister(int id) => UnregisterHotKey(_source.Handle, id);

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            Pressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
