using BatchPad.Core.Running;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class RunHandleOutputTests
{
    [TestMethod]
    public async Task LateSubscribersGetTheKeptLinesThenLiveOnesInOrder()
    {
        const int count = RunHandle.KeptLines + 10_000;
        using var run = PrintNumbers(count);
        var early = Collector.Subscribe(run);
        await Eventually(() => early.Count > 1000);
        var middle = Collector.Subscribe(run);
        await Task.WhenAll(early.Ended, middle.Ended).WaitAsync(Limit);
        var late = Collector.Subscribe(run);

        AssertContiguousTo(count - 1, early.Numbers());
        AssertContiguousTo(count - 1, middle.Numbers());
        Assert.IsTrue(late.Ended.IsCompleted);
        Assert.AreEqual($"… {count - RunHandle.KeptLines} earlier lines not kept", late.Lines[0].Text);
        CollectionAssert.AreEqual(Enumerable.Range(count - RunHandle.KeptLines, RunHandle.KeptLines).ToArray(), late.Numbers());
        CollectionAssert.AreEqual(late.Lines, run.Output.ToList());
    }

    [TestMethod]
    public async Task AThrowingSubscriberDoesNotStopTheOthers()
    {
        using var run = PrintNumbers(100);
        using var failing = run.Subscribe(_ => throw new InvalidOperationException("subscriber bug"));
        var collector = Collector.Subscribe(run);

        await collector.Ended.WaitAsync(Limit);

        CollectionAssert.AreEqual(Enumerable.Range(0, 100).ToArray(), collector.Numbers());
        Assert.AreEqual(0, (await run.Completion.WaitAsync(Limit)).ExitCode);
    }

    [TestMethod]
    public async Task ASubscriberCanReadTheRunFromAnotherThread()
    {
        using var run = PrintNumbers(10);
        var answered = new List<bool>();
        using var subscription = run.Subscribe(_ => answered.Add(Task.Run(() => run.Output.Count + run.ProcessId).Wait(TimeSpan.FromSeconds(5))));
        var collector = Collector.Subscribe(run);

        await collector.Ended.WaitAsync(Limit);

        Assert.IsNotEmpty(answered);
        Assert.IsTrue(answered.All(a => a));
    }

    private static RunHandle PrintNumbers(int count)
    {
        var python = new InterpreterLocator().Python() ?? throw new AssertInconclusiveException("Python is not installed.");
        var command = new CommandLine(python.Path,
            ArgvQuoter.Join([.. python.LeadingArguments, "-c", $"import sys; sys.stdout.write(''.join(f'{{i}}\\n' for i in range({count})))"]),
            Path.GetTempPath());
        return ProcessRunner.Start([new RunSpec(command, EnvironmentBuilder.FromCurrentProcess().Build())]);
    }

    private static void AssertContiguousTo(int last, int[] numbers)
    {
        Assert.IsNotEmpty(numbers);
        Assert.AreEqual(last, numbers[^1]);
        CollectionAssert.AreEqual(Enumerable.Range(numbers[0], numbers.Length).ToArray(), numbers);
    }

    private sealed class Collector : IObserver<OutputLine>
    {
        private readonly List<OutputLine> _lines = [];
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static Collector Subscribe(RunHandle run)
        {
            var collector = new Collector();
            run.Subscribe(collector);
            return collector;
        }

        public Task Ended => _ended.Task;

        public int Count
        {
            get
            {
                lock (_lines)
                    return _lines.Count;
            }
        }

        public List<OutputLine> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }

        public int[] Numbers() => [.. Lines.Where(l => l.Stream == OutputStream.Stdout).Select(l => int.Parse(l.Text))];

        public void OnNext(OutputLine value)
        {
            lock (_lines)
                _lines.Add(value);
        }

        public void OnCompleted() => _ended.TrySetResult();

        public void OnError(Exception error) { }
    }
}
