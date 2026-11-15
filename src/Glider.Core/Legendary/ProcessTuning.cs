using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Glider.Core.Services;

namespace Glider.Core.Legendary;

/// <summary>CPU priority class of a process (the values of SetPriorityClass).</summary>
internal enum CpuPriority : uint
{
    Normal = 0x00000020,
    BelowNormal = 0x00004000,
    Idle = 0x00000040,
}

/// <summary>I/O priority hint (NtSetInformationProcess ProcessIoPriority): 0 very low, 1 low, 2 normal.</summary>
internal enum IoPriority : uint
{
    VeryLow = 0,
    Low = 1,
    Normal = 2,
}

/// <summary>Memory (page) priority (SetProcessInformation ProcessMemoryPriority): 1 very low, 2 low, 5 normal.</summary>
internal enum MemoryPriority : uint
{
    VeryLow = 1,
    Low = 2,
    Normal = 5,
}

/// <summary>What one process should look like in a download mode. <see cref="EcoQos"/> = Windows' "efficiency mode" (power throttling).</summary>
internal readonly record struct ProcessSettings(CpuPriority Cpu, IoPriority Io, MemoryPriority Memory, bool EcoQos)
{
    public static ProcessSettings Normal { get; } = new(CpuPriority.Normal, IoPriority.Normal, MemoryPriority.Normal, EcoQos: false);
}

/// <summary>
/// Per-process priorities by PID (docs/download-perf.md §5.6): CPU class, I/O priority, memory priority and EcoQoS. Everything
/// here only <em>lowers</em> resources or puts them back to normal, which needs no privilege for our own children. The job
/// object's priority-class limit is deliberately not used in mode-aware runs: it would forbid raising a process again when the
/// mode changes live.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessTuning
{
    private const uint ProcessSetInformation = 0x0200;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSuspendResume = 0x0800;
    private const int ProcessIoPriorityClass = 33;
    private const int ProcessMemoryPriorityClass = 0;
    private const int ProcessPowerThrottlingClass = 4;
    private const uint PowerThrottlingExecutionSpeed = 1;

    /// <summary>Applies <paramref name="settings"/> to the process; false when it exited or can't be opened (nothing to tune).</summary>
    public static bool TryApply(int pid, ProcessSettings settings)
    {
        var h = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var ok = SetPriorityClass(h, (uint)settings.Cpu);
            var io = (uint)settings.Io;
            ok &= NtSetInformationProcess(h, ProcessIoPriorityClass, ref io, sizeof(uint)) >= 0;
            var memory = new MemoryPriorityInformation { MemoryPriority = (uint)settings.Memory };
            ok &= SetProcessInformation(h, ProcessMemoryPriorityClass, ref memory, (uint)Marshal.SizeOf<MemoryPriorityInformation>());
            var power = new PowerThrottlingState { Version = 1, ControlMask = PowerThrottlingExecutionSpeed, StateMask = settings.EcoQos ? PowerThrottlingExecutionSpeed : 0 };
            SetProcessInformation(h, ProcessPowerThrottlingClass, ref power, (uint)Marshal.SizeOf<PowerThrottlingState>()); // Windows 11 only for some builds: best effort
            return ok;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>What the OS reports for the process right now (tests and diagnostics); null when it can't be opened.</summary>
    public static ProcessSettings? Query(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var cpu = (CpuPriority)GetPriorityClass(h);
            uint io = 2;
            if (NtQueryInformationProcess(h, ProcessIoPriorityClass, ref io, sizeof(uint), out _) < 0)
            {
                io = 2;
            }
            var memory = new MemoryPriorityInformation { MemoryPriority = 5 };
            GetProcessInformation(h, ProcessMemoryPriorityClass, ref memory, (uint)Marshal.SizeOf<MemoryPriorityInformation>());
            var power = new PowerThrottlingState { Version = 1 };
            GetProcessInformation(h, ProcessPowerThrottlingClass, ref power, (uint)Marshal.SizeOf<PowerThrottlingState>());
            return new ProcessSettings(cpu, (IoPriority)io, (MemoryPriority)memory.MemoryPriority, (power.StateMask & PowerThrottlingExecutionSpeed) != 0);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>Process creation time (UTC ticks), or null.</summary>
    public static long? CreationTicks(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return GetProcessTimes(h, out var created, out _, out _, out _) ? created : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static bool Suspend(int pid) => SuspendResume(pid, suspend: true);

    public static bool Resume(int pid) => SuspendResume(pid, suspend: false);

    private static bool SuspendResume(int pid, bool suspend)
    {
        var h = OpenProcess(ProcessSuspendResume, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return (suspend ? NtSuspendProcess(h) : NtResumeProcess(h)) >= 0;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>PIDs of the processes currently in the job (QueryInformationJobObject, JobObjectBasicProcessIdList).</summary>
    public static IReadOnlyList<int> JobProcessIds(SafeJobHandle job)
    {
        const int capacity = 512;
        var size = 8 + capacity * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, 3, buffer, (uint)size, out _))
            {
                return [];
            }

            var count = (int)Math.Min((uint)Marshal.ReadInt32(buffer, 4), capacity);
            var ids = new int[count];
            for (var i = 0; i < count; i++)
            {
                ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
            }

            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryPriorityInformation
    {
        public uint MemoryPriority;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref MemoryPriorityInformation info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref MemoryPriorityInformation info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref uint info, uint size);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref uint info, uint size, out uint returned);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtResumeProcess(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeJobHandle job, int infoClass, IntPtr info, uint size, out uint returned);
}

/// <summary>
/// Who is who inside a Legendary install/update/repair tree, from process ids alone (docs/download-perf.md §5.6). The
/// DLManager announces its own pid ("Download Manager running with process-id"); it spawns the download workers and then the file
/// writer, which is its <em>last</em> child. The bundled onedir build has one process per multiprocessing child; a onefile build would have
/// a bootloader with the real interpreter beneath each one, so the writer is taken as the newest direct child plus everything below it.
/// </summary>
internal static class ProcessRoles
{
    /// <summary>Pids of the writer subtree (the newest child of the DLManager and its descendants), or empty while it isn't up yet.</summary>
    public static IReadOnlySet<int> Writer(IReadOnlyCollection<int> jobPids, int? downloadManager, IReadOnlyList<ProcessEntry> snapshot, Func<int, long?> creationTime)
    {
        if (downloadManager is not { } dlm || !jobPids.Contains(dlm))
        {
            return new HashSet<int>();
        }

        var inJob = jobPids.ToHashSet();
        var children = snapshot.Where(p => p.ParentId == dlm && inJob.Contains(p.Id) && p.Id != dlm).Select(p => p.Id).ToList();
        if (children.Count == 0)
        {
            return new HashSet<int>();
        }

        var newest = children.OrderByDescending(id => creationTime(id) ?? long.MinValue).ThenByDescending(id => id).First();
        return Descendants(newest, inJob, snapshot);
    }

    /// <summary>The download workers: the DLManager's other children and what is below them.</summary>
    public static IReadOnlySet<int> Workers(IReadOnlyCollection<int> jobPids, int? downloadManager, IReadOnlySet<int> writer, IReadOnlyList<ProcessEntry> snapshot)
    {
        var workers = new HashSet<int>();
        if (downloadManager is not { } dlm)
        {
            return workers;
        }

        var inJob = jobPids.ToHashSet();
        foreach (var child in snapshot.Where(p => p.ParentId == dlm && inJob.Contains(p.Id) && !writer.Contains(p.Id)))
        {
            workers.UnionWith(Descendants(child.Id, inJob, snapshot));
        }

        return workers;
    }

    private static HashSet<int> Descendants(int root, HashSet<int> inJob, IReadOnlyList<ProcessEntry> snapshot)
    {
        var result = new HashSet<int> { root };
        var queue = new Queue<int>([root]);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var p in snapshot)
            {
                if (p.ParentId == parent && inJob.Contains(p.Id) && result.Add(p.Id))
                {
                    queue.Enqueue(p.Id);
                }
            }
        }

        return result;
    }
}
