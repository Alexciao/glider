using Glider.Core.Contracts;
using Velopack;
using Velopack.Sources;

namespace Glider.App;

/// <summary>
/// Background self-update from GitHub Releases (Velopack). Downloads quietly, tells the UI once, and applies the update
/// when Glider next exits. Skipped for dev builds, portable runs and test runs; every failure is logged and swallowed.
/// </summary>
internal static class AppUpdater
{
    private const string RepoUrl = "https://github.com/Alexciao/glider";
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    public static void Start(ForwardingEventSink sink)
    {
        UpdateManager manager;
        try
        {
            manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
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
            manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
            sink.Publish(RpcEvents.Notice, new Notice("app-update", NoticeLevel.Info, $"Glider {version} is ready", "It installs the next time you close Glider.", null, null, null));
            HostLog.Info($"Self-update {version} downloaded; applies on exit");
            return true;
        }
        catch (Exception ex)
        {
            HostLog.Warn("Self-update check failed", ex);
            return false;
        }
    }
}
