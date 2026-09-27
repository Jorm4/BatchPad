using System.Windows;
using System.Windows.Shell;

namespace BatchPad.App.Services;

public interface IJumpListService
{
    void Apply(IReadOnlyList<JumpListItem> items);
}

/// <summary>The jump list belongs to the exe, so a stable and a dev build each keep their own.</summary>
public sealed class JumpListService : IJumpListService
{
    private IReadOnlyList<JumpListItem> _applied = [];

    public void Apply(IReadOnlyList<JumpListItem> items)
    {
        if (items.SequenceEqual(_applied) || Application.Current is not { } app)
            return;
        _applied = items;
        var exe = Environment.ProcessPath;
        var list = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
        foreach (var item in items)
            list.JumpItems.Add(new JumpTask
            {
                Title = item.Title,
                Arguments = item.Arguments,
                Description = item.Description,
                CustomCategory = item.Category,
                ApplicationPath = exe,
                IconResourcePath = exe,
            });
        JumpList.SetJumpList(app, list);
        list.Apply();
    }
}
