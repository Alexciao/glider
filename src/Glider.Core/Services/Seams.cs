using System.Diagnostics;
using Glider.Core.Contracts;

// Seams owned by the service layer (not shared with the parallel workstreams). Each has a real
// implementation here and a hand-written fake in the tests.

namespace Glider.Core.Services;

/// <summary>A running process we care about (game, launcher stub).</summary>
public sealed record RunningProcess(int Id, string Name, DateTimeOffset? StartTime);

/// <summary>One row of a process snapshot (Toolhelp): enough to follow a process tree.</summary>
public sealed record ProcessEntry(int Id, int ParentId, string Name, DateTimeOffset? StartTime);

/// <summary>Cheap process lookups (no handles kept open).</summary>
public interface IProcessMonitor
{
    /// <summary>Processes whose name (without .exe) equals <paramref name="processName"/>.</summary>
    IReadOnlyList<RunningProcess> Find(string processName);

    /// <summary>Every process with its parent id (one Toolhelp snapshot), for process-tree tracking.</summary>
    IReadOnlyList<ProcessEntry> Snapshot();

    /// <summary>Start time of a process (null when it exited or can't be opened).</summary>
    DateTimeOffset? StartTimeOf(int pid) => null;

    /// <summary>Asks the process to close, then kills it (and its children) if it has not exited shortly after.</summary>
    void Close(RunningProcess process);
}

/// <summary>Spawns the game from the command Legendary resolved. Never logs arguments (they carry a live exchange code).</summary>
public interface IGameProcessStarter
{
    RunningProcess Start(LaunchCommand command);
}

/// <summary>Is a Windows service registered? Used for the Easy Anti-Cheat check.</summary>
public interface IServiceQuery
{
    bool IsInstalled(string serviceName);
}

/// <summary>Is this executable signed by its expected publisher? Checked before anything is run elevated.</summary>
public interface IPublisherCheck
{
    /// <summary>Null when trusted; otherwise why not (shown in the error detail).</summary>
    string? WhyUntrusted(string path);
}

public sealed record FixedDrive(string RootPath, long FreeBytes, long TotalBytes, bool? IsSsd = null);

public interface IDiskInfo
{
    IReadOnlyList<FixedDrive> FixedDrives();

    /// <summary>Free bytes on the volume holding <paramref name="path"/> (the path need not exist yet); null if unknown.</summary>
    long? FreeBytes(string path);
}

/// <summary>Optional host signal: how long the user has been idle (no input). Absent = treat the user as idle.</summary>
public interface IIdleSignal
{
    TimeSpan IdleTime { get; }
}

// ===================================== Default implementations =====================================

public sealed class SystemProcessMonitor : IProcessMonitor
{
    public IReadOnlyList<RunningProcess> Find(string processName)
    {
        var result = new List<RunningProcess>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                DateTimeOffset? start = null;
                try
                {
                    start = new DateTimeOffset(p.StartTime);
                }
                catch (Exception)
                {
                    // Access denied (elevated / protected process) — the start time is cosmetic.
                }

                result.Add(new RunningProcess(p.Id, processName, start));
            }
        }

        return result;
    }

    public IReadOnlyList<ProcessEntry> Snapshot() => Game.ProcessSnapshot.Take();

    public DateTimeOffset? StartTimeOf(int pid) => Game.ProcessSnapshot.StartTime(pid);

    public void Close(RunningProcess process)
    {
        try
        {
            using var p = Process.GetProcessById(process.Id);
            if (!string.Equals(p.ProcessName, process.Name, StringComparison.OrdinalIgnoreCase))
            {
                return; // pid reused by something else
            }

            if (p.CloseMainWindow() && p.WaitForExit(3000))
            {
                return;
            }

            p.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or protected/elevated: nothing more we can do.
        }
    }
}

public sealed class SystemGameProcessStarter : IGameProcessStarter
{
    public RunningProcess Start(LaunchCommand command)
    {
        var psi = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
        };
        foreach (var a in command.Arguments)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (k, v) in command.Environment)
        {
            psi.Environment[k] = v;
        }

        using var p = Process.Start(psi) ?? throw new GliderException(Errors.LaunchFailed("Process.Start returned null"));
        return new RunningProcess(p.Id, Path.GetFileNameWithoutExtension(command.Executable), DateTimeOffset.Now);
    }
}

/// <summary>Registry lookup of HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt; — no process spawn, ~µs.</summary>
public sealed class RegistryServiceQuery : IServiceQuery
{
    public bool IsInstalled(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        return key is not null;
    }
}

public sealed class SystemDiskInfo : IDiskInfo
{
    public IReadOnlyList<FixedDrive> FixedDrives()
    {
        var list = new List<FixedDrive>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                {
                    list.Add(new FixedDrive(d.RootDirectory.FullName, d.AvailableFreeSpace, d.TotalSize, DriveMedia.IsSsd(d.RootDirectory.FullName)));
                }
            }
            catch (IOException)
            {
                // Drive vanished between enumeration and query.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return list;
    }

    public long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var d = new DriveInfo(root);
            return d.IsReady ? d.AvailableFreeSpace : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Bridges construction cycles: the host creates the dispatcher (an IEventSink) after the service.</summary>
public sealed class DeferredEventSink : IEventSink
{
    public IEventSink? Target { get; set; }

    public void Publish<T>(string eventName, T data) => Target?.Publish(eventName, data);
}

/// <summary>Bridges construction cycles: the job queue needs hooks, the hooks come from GliderService (which needs the queue).</summary>
public sealed class DeferredJobHooks : IJobHooks
{
    public IJobHooks? Target { get; set; }

    private IJobHooks Hooks => Target ?? throw new InvalidOperationException("DeferredJobHooks.Target not set");

    public Task BeforeRunAsync(JobInfo job, Action<string?> setBlockedReason, CancellationToken ct) => Hooks.BeforeRunAsync(job, setBlockedReason, ct);

    public Task OnSucceededAsync(JobInfo job, CancellationToken ct) => Hooks.OnSucceededAsync(job, ct);


    public Task OnFailedAsync(JobInfo job, CancellationToken ct) => Hooks.OnFailedAsync(job, ct);
}
