using System.Diagnostics;
using System.Runtime.InteropServices;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// One Toolhelp32 snapshot of every process with its parent id (Windows; no handles kept, ~1 ms). Elsewhere: names only,
/// parent 0. Start times are not read here (that needs a handle per process); the session tracker asks for them only for
/// the few processes it is about to adopt.
/// </summary>
internal static class ProcessSnapshot
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static IReadOnlyList<ProcessEntry> Take()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fallback();
        }

        var list = new List<ProcessEntry>(512);
        var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == InvalidHandle || snap == IntPtr.Zero)
        {
            return Fallback();
        }

        try
        {
            var entry = new ProcessEntry32W { Size = (uint)Marshal.SizeOf<ProcessEntry32W>() };
            if (!Process32FirstW(snap, ref entry))
            {
                return list;
            }

            do
            {
                var name = entry.ExeFile ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^4];
                }

                list.Add(new ProcessEntry((int)entry.ProcessId, (int)entry.ParentProcessId, name, null));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32W>();
            }
            while (Process32NextW(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }

        return list;
    }

    /// <summary>Process start time, or null (exited, or protected/elevated so we can't open it).</summary>
    public static DateTimeOffset? StartTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return new DateTimeOffset(p.StartTime);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static List<ProcessEntry> Fallback()
    {
        var list = new List<ProcessEntry>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                list.Add(new ProcessEntry(p.Id, 0, p.ProcessName, null));
            }
        }

        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
