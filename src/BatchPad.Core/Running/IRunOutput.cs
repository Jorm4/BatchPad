namespace BatchPad.Core.Running;

/// <summary>A started run's output and result: a <see cref="RunHandle"/>, or the UI's stand-in for one.</summary>
public interface IRunOutput
{
    IDisposable Subscribe(Action<OutputLine> onLine);
    Task<RunResult> Completion { get; }

    /// <summary>The run behind a stand-in, for its start time, queue time and kept output.</summary>
    RunHandle? Handle => null;
}
