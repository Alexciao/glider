using System.Runtime;
using Glider.App.Native;

namespace Glider.App;

/// <summary>Gives memory back after idling hidden: full compacting GC, then drop the process working set (pages fault back on demand).</summary>
internal static class MemoryTrimmer
{
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Win32.K32EmptyWorkingSet(Win32.GetCurrentProcess());
    }
}
