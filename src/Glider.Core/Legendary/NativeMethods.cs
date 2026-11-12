using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Glider.Core.Legendary;

internal static class NativeMethods
{
    public const uint JobObjectLimitPriorityClass = 0x00000020;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;
    public const uint BelowNormalPriorityClass = 0x00004000;
    public const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern uint GetACP();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JobObjectExtendedLimitInformationData info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

/// <summary>
/// A Windows Job Object with KILL_ON_JOB_CLOSE: Legendary's PyInstaller bootloader, its Python child, the DLManager and
/// every DLWorker/FileWorker process land in the job, so terminating (or merely closing) it kills the whole tree —
/// also if Glider itself crashes, since the OS closes the handle. Optionally pins the job to below-normal priority so
/// downloads and hashing never starve the foreground (the limit applies to future children too).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobObject : IDisposable
{
    private readonly SafeJobHandle _handle;

    private WindowsJobObject(SafeJobHandle handle) => _handle = handle;

    public static WindowsJobObject? TryCreate(bool belowNormalPriority, ILog log)
    {
        var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            log.Warn("legendary", $"CreateJobObject failed (error {Marshal.GetLastWin32Error()}); falling back to tree kill.");
            handle.Dispose();
            return null;
        }

        var info = new NativeMethods.JobObjectExtendedLimitInformationData();
        info.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
        if (belowNormalPriority)
        {
            info.BasicLimitInformation.LimitFlags |= NativeMethods.JobObjectLimitPriorityClass;
            info.BasicLimitInformation.PriorityClass = NativeMethods.BelowNormalPriorityClass;
        }

        if (!NativeMethods.SetInformationJobObject(handle, NativeMethods.JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformationData>()))
        {
            log.Warn("legendary", $"SetInformationJobObject failed (error {Marshal.GetLastWin32Error()}); falling back to tree kill.");
            handle.Dispose();
            return null;
        }

        return new WindowsJobObject(handle);
    }

    public bool TryAssign(Process process) => NativeMethods.AssignProcessToJobObject(_handle, process.SafeHandle);

    public void Terminate()
    {
        if (!_handle.IsClosed)
        {
            NativeMethods.TerminateJobObject(_handle, 1);
        }
    }

    public void Dispose() => _handle.Dispose();
}
