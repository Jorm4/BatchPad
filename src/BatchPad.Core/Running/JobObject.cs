using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BatchPad.Core.Running;

/// <summary>A Windows Job Object holding a run's whole process tree, detached children included (§4 Stopping).</summary>
internal sealed unsafe class JobObject : IDisposable
{
    private const int ErrorMoreData = 234;

    private readonly SafeJobHandle _handle;

    public JobObject()
    {
        _handle = NativeMethods.CreateJobObject(0, null);
        if (_handle.IsInvalid)
            throw new Win32Exception();
    }

    public void Assign(nint process)
    {
        if (!NativeMethods.AssignProcessToJobObject(_handle, process))
            throw new Win32Exception();
    }

    public void Terminate(uint exitCode) => NativeMethods.TerminateJobObject(_handle, exitCode);

    public IReadOnlyList<int> ProcessIds()
    {
        for (var capacity = 64; ; capacity *= 4)
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counts, then ULONG_PTR ids.
            var buffer = new byte[2 * sizeof(int) + capacity * sizeof(nint)];
            fixed (byte* list = buffer)
            {
                if (NativeMethods.QueryInformationJobObject(_handle, NativeMethods.JobObjectBasicProcessIdList,
                        list, buffer.Length, out _))
                {
                    var count = ((int*)list)[1];
                    var ids = (nint*)(list + 2 * sizeof(int));
                    var result = new int[count];
                    for (var i = 0; i < count; i++)
                        result[i] = (int)ids[i];
                    return result;
                }
            }
            if (Marshal.GetLastPInvokeError() != ErrorMoreData)
                return [];
        }
    }

    /// <summary>Posts WM_CLOSE to the visible top-level windows of the job's processes; returns how many.</summary>
    public int CloseWindows()
    {
        var state = new WindowSearch([.. ProcessIds().Select(id => (uint)id)]);
        var handle = GCHandle.Alloc(state);
        try
        {
            NativeMethods.EnumWindows(&CollectWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        foreach (var window in state.Windows)
            NativeMethods.PostMessage(window, NativeMethods.WmClose, 0, 0);
        return state.Windows.Count;
    }

    public void Dispose() => _handle.Dispose();

    private sealed record WindowSearch(HashSet<uint> ProcessIds)
    {
        public List<nint> Windows { get; } = [];
    }

    [UnmanagedCallersOnly]
    private static int CollectWindow(nint window, nint parameter)
    {
        var search = (WindowSearch)GCHandle.FromIntPtr(parameter).Target!;
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (search.ProcessIds.Contains(processId) && NativeMethods.IsWindowVisible(window))
            search.Windows.Add(window);
        return 1;
    }
}
