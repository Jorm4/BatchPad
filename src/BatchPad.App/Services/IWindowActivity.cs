namespace BatchPad.App.Services;

public interface IWindowActivity
{
    bool IsActive { get; }

    event Action? Activated;
}
