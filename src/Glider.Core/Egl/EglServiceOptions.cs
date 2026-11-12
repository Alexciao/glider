using System.Diagnostics;

namespace Glider.Core.Egl;

/// <summary>Looks up running processes by name. Faked in tests.</summary>
public interface IProcessProbe
{
    bool IsRunning(string processName);
}

public sealed class SystemProcessProbe : IProcessProbe
{
    public static readonly SystemProcessProbe Instance = new();

    public bool IsRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            return procs.Length > 0;
        }
        finally
        {
            foreach (var p in procs)
            {
                p.Dispose();
            }
        }
    }
}

/// <summary>Tunables for <see cref="EglService"/>. Defaults are the production values; tests shorten them.</summary>
public sealed class EglServiceOptions
{
    /// <summary>EGL's main process (EpicGamesLauncher.exe).</summary>
    public string LauncherProcessName { get; init; } = "EpicGamesLauncher";

    /// <summary>Staging evidence (.egstore\bps, .egstore\Pending, Manifests\Pending) counts only if touched this recently.</summary>
    public TimeSpan BusyRecentWindow { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Fallback poll while waiting for EGL (file watchers wake us sooner).</summary>
    public TimeSpan BusyPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Attempts per EGL file write when it's locked (sharing violation).</summary>
    public int WriteAttempts { get; init; } = 5;

    public TimeSpan WriteRetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Backup sets kept under backups\egl (oldest pruned).</summary>
    public int BackupsToKeep { get; init; } = 20;

    /// <summary>Upper bound of directory entries inspected per staging folder (keeps busy checks cheap).</summary>
    public int MaxStagingEntriesInspected { get; init; } = 256;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Test hook: invoked after the temp file is written, just before it replaces the target.</summary>
    internal Action<string>? BeforeCommitHook { get; init; }
}
