using Glider.Core.Contracts;
using Velopack;
using Velopack.Sources;

namespace Glider.App;

/// <summary>
/// Background self-update from GitHub Releases (Velopack). Downloads quietly, tells the UI once (with a Restart button), and
/// installs the update when Glider really exits: Quit, or Restart from the notice. Skipped for dev builds, portable runs and
/// test runs; every failure is logged and swallowed.
/// </summary>
/// <remarks>
/// Velopack's updater is only started at exit. Started right after the download (as before v0.2.5), it waits 60 s for Glider
/// to exit, then closes it and installs anyway, without restarting it: Glider, which lives in the tray, just vanished a minute
/// after an update arrived.
/// </remarks>
internal static class AppUpdater
{
    private static readonly Lock Gate = new();
    private static UpdateManager? _manager;
    private static VelopackAsset? _staged;
    private static bool _restart;
    private static bool _handedOver;

    private const string RepoUrl = "https://github.com/Alexciao/glider";
    private static readonly TimeSpan FirstDelay = int.TryParse(Environment.GetEnvironmentVariable("GLIDER_UPDATE_DELAY_S"), out var s) ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    public static void Start(ForwardingEventSink sink)
    {
        UpdateManager manager;
        try
        {
            // GLIDER_UPDATE_SOURCE=<folder>: a local feed (vpk output) instead of GitHub Releases, for testing the update flow.
            IUpdateSource source = Environment.GetEnvironmentVariable("GLIDER_UPDATE_SOURCE") is { Length: > 0 } local
                ? new SimpleFileSource(new DirectoryInfo(local))
                : new GithubSource(RepoUrl, null, false);
            manager = new UpdateManager(source);
            if (!manager.IsInstalled || manager.IsPortable)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            HostLog.Warn("Self-update unavailable", ex);
            return;
        }

        // Background thread-pool work; the process exiting ends it.
        _ = Task.Run(async () =>
        {
            await Task.Delay(FirstDelay).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                if (await CheckOnceAsync(manager, sink).ConfigureAwait(false))
                {
                    return; // an update is staged; nothing more to do until restart
                }
            }
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false));
        });
    }

    private static async Task<bool> CheckOnceAsync(UpdateManager manager, ForwardingEventSink sink)
    {
        try
        {
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return false;
            }

            var version = update.TargetFullRelease.Version;
            HostLog.Info($"Self-update {version} found; downloading");
            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            lock (Gate)
            {
                _manager = manager;
                _staged = update.TargetFullRelease;
            }

            sink.Publish(RpcEvents.Notice, new Notice("app-update", NoticeLevel.Info, $"Glider {version} is ready", "Restart Glider to install it, or it installs when you quit.", FixAction.RestartToUpdate, null, null));
            HostLog.Info($"Self-update {version} downloaded; installs on quit or restart");
            return true;
        }
        catch (Exception ex)
        {
            HostLog.Warn("Self-update check failed", ex);
            return false;
        }
    }

    /// <summary>The next exit should start the new version once it is installed (the notice's Restart button).</summary>
    public static void RestartAfterExit()
    {
        lock (Gate)
        {
            _restart = true;
        }
    }

    /// <summary>
    /// Called on the way out (UI thread, just before shutdown): starts Velopack's updater, which waits for this process to exit,
    /// installs the staged update and, after Restart, starts the new version. No-op when nothing is staged.
    /// </summary>
    public static void ApplyAfterExit()
    {
        UpdateManager? manager;
        VelopackAsset? staged;
        bool restart;
        lock (Gate)
        {
            if (_handedOver || _manager is null || _staged is null)
            {
                return;
            }

            _handedOver = true;
            (manager, staged, restart) = (_manager, _staged, _restart);
        }

        try
        {
            // The restarted version keeps a non-default data folder (Velopack restarts the exe without the original arguments).
            var args = Environment.GetCommandLineArgs();
            var dataDir = args.FirstOrDefault(a => a.StartsWith("--data-dir=", StringComparison.Ordinal));
            manager.WaitExitThenApplyUpdates(staged, silent: true, restart: restart, restartArgs: dataDir is null ? null : [dataDir]);
            HostLog.Info($"Self-update {staged.Version}: installing after exit{(restart ? ", then restarting" : "")}");
        }
        catch (Exception ex)
        {
            HostLog.Warn("Self-update: handing over to the updater failed", ex);
        }
    }
}
