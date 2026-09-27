using BatchPad.App.ViewModels;
using BatchPad.Core.Model;

namespace BatchPad.App.Services;

public sealed class HotkeyService
{
    private readonly IHotkeyApi _api;
    private readonly Action<string> _run;
    private readonly Dictionary<int, Binding> _registered = [];
    private readonly Dictionary<string, HotkeyGesture> _taken = [];
    private int _nextId = 1;
    private TreeViewModel? _tree;
    private bool _trusted;
    private bool _suspended;

    public HotkeyService(IHotkeyApi api, Action<string> run)
    {
        _api = api;
        _run = run;
        api.Pressed += OnPressed;
    }

    private sealed record Binding(string NodeKey, HotkeyGesture Gesture);

    /// <summary>Registers nothing in an untrusted workspace, which can't run anything; a gesture used twice goes to the first node.</summary>
    public void Apply(TreeViewModel? tree, bool trusted)
    {
        _tree = tree;
        _trusted = trusted;
        IEnumerable<NodeViewModel> nodes = trusted && !_suspended ? tree?.AllNodes ?? [] : [];
        var wanted = nodes
            .Where(n => n is { Hotkey: not null, IsBroken: false })
            .Select(n => new Binding(n.Key, n.Hotkey!))
            .DistinctBy(b => b.Gesture)
            .ToHashSet();
        foreach (var (id, binding) in _registered.Where(r => !wanted.Contains(r.Value)).ToList())
        {
            _api.Unregister(id);
            _registered.Remove(id);
        }
        _taken.Clear();
        var held = _registered.Values.ToHashSet();
        foreach (var binding in wanted.Where(b => !held.Contains(b)))
        {
            var id = _nextId++;
            if (_api.Register(id, binding.Gesture))
                _registered[id] = binding;
            else
                _taken[binding.NodeKey] = binding.Gesture;
        }
    }

    public void Clear() => Apply(null, trusted: false);

    /// <summary>Frees BatchPad's own keys so a hotkey box can record them instead of running their scripts.</summary>
    public void Suspend()
    {
        _suspended = true;
        Apply(_tree, _trusted);
    }

    public void Resume()
    {
        if (!_suspended)
            return;
        _suspended = false;
        Apply(_tree, _trusted);
    }

    public string? ProblemFor(string? nodeKey) =>
        nodeKey is not null && _taken.TryGetValue(nodeKey, out var gesture) ? HotkeyMessages.Taken(gesture) : null;

    /// <summary>Whether another program holds <paramref name="gesture"/>, trying it when BatchPad hasn't.</summary>
    public bool IsTaken(HotkeyGesture gesture)
    {
        if (_taken.ContainsValue(gesture))
            return true;
        if (_registered.Values.Any(b => b.Gesture == gesture))
            return false;
        var probe = _nextId++;
        if (!_api.Register(probe, gesture))
            return true;
        _api.Unregister(probe);
        return false;
    }

    private void OnPressed(int id)
    {
        if (_registered.TryGetValue(id, out var binding))
            _run(binding.NodeKey);
    }
}
