using BatchPad.Core.Model;

namespace BatchPad.App.Services;

public interface IHotkeyApi
{
    event Action<int>? Pressed;

    /// <summary>False when another program already holds the combination.</summary>
    bool Register(int id, HotkeyGesture gesture);

    void Unregister(int id);
}
