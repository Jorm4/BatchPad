using BatchPad.Core.Config;

namespace BatchPad.App.Services;

public static class IoProblems
{
    public static bool IsIoProblem(Exception ex) => ex is IOException or UnauthorizedAccessException or ConfigException;

    /// <summary>Runs <paramref name="action"/>, ignoring a failed read or write: the caller has nothing to show for it.</summary>
    public static void TryIo(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsIoProblem(ex))
        {
        }
    }
}
