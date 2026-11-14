using System.Windows.Threading;
using Glider.Core.Contracts;
using Glider.Core.Links;
using Glider.Core.Services;
using Glider.Core.Tray;

namespace Glider.App;

/// <summary>
/// Feeds the tray from the same events the UI gets (latest snapshot and job progress, captured with two reference writes on the
/// publisher's thread), refreshes the tooltip at most once a second, builds the menu model when the menu opens and runs the chosen item.
/// Nothing here parses the library or touches Legendary; the UI thread only draws.
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private static readonly TimeSpan TooltipDelay = TimeSpan.FromSeconds(1);

    private readonly HostRuntime _rt;
    private readonly Dispatcher _ui;
    private readonly Timer _tooltipTimer;
    private TrayIcon? _tray;
    private volatile AppSnapshot? _snapshot;
    private volatile JobProgress? _progress;
    private int _tooltipScheduled;

    public TrayHost(HostRuntime rt)
    {
        _rt = rt;
        _ui = rt.Ui;
        _tooltipTimer = new Timer(_ => RefreshTooltip(), null, Timeout.Infinite, Timeout.Infinite);
        rt.Sink.Tapped += OnEvent;
    }

    public void Attach(TrayIcon tray) => _tray = tray;

    private void OnEvent(string name, object? data)
    {
        switch (name)
        {
            case RpcEvents.State when data is AppSnapshot s:
                _snapshot = s;
                break;
            case RpcEvents.Progress when data is JobProgress p:
                _progress = p;
                break;
            default:
                return;
        }

        if (Interlocked.Exchange(ref _tooltipScheduled, 1) == 0)
        {
            _tooltipTimer.Change(TooltipDelay, Timeout.InfiniteTimeSpan); // trailing edge: the last change always lands
        }
    }

    private void RefreshTooltip()
    {
        Interlocked.Exchange(ref _tooltipScheduled, 0);
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        var text = TrayMenuModel.Tooltip(snapshot, _progress);
        _ui.BeginInvoke(() => _tray?.SetTooltip(text));
    }

    /// <summary>Called on the UI thread when the menu opens: one pass over the library for the recent games, the snapshot is already cached.</summary>
    public TrayMenu BuildMenu()
    {
        var service = Composition.Runtime?.Service;
        var snapshot = _snapshot ?? service?.Hub.BuildSnapshot();
        return snapshot is null
            ? new TrayMenu([TrayItem.Cmd("Open Glider", TrayCommand.Open, isDefault: true), TrayItem.Separator, TrayItem.Cmd("Quit Glider", TrayCommand.Quit)], "Glider")
            : TrayMenuModel.Build(snapshot, service?.LibraryGames ?? [], _progress);
    }

    public void Run(TrayItem item)
    {
        switch (item.Command)
        {
            case TrayCommand.Open:
                _rt.Platform.RestoreWindow();
                break;
            case TrayCommand.Launch when item.Arg is { } app:
                HostApp.FireAndForget(r => r.Launch(new LaunchRequest(app, null, false)), "Play");
                break;
            case TrayCommand.Close when item.Arg is { } app:
                HostApp.FireAndForget(r => r.CloseGame(new AppRef(app)), "Close game");
                break;
            case TrayCommand.PauseAll:
                HostApp.FireAndForget(r => r.PauseQueue(), "Pause downloads");
                break;
            case TrayCommand.ResumeAll:
                HostApp.FireAndForget(r => r.ResumeQueue(), "Resume downloads");
                break;
            case TrayCommand.SetMode when TrayMenuModel.TryParseMode(item.Arg, out var mode):
                HostApp.FireAndForget(
                    async r =>
                    {
                        var current = (await r.GetSnapshot().ConfigureAwait(false)).Settings;
                        await r.UpdateSettings(current with { DownloadMode = mode }).ConfigureAwait(false);
                    },
                    "Download mode");
                break;
            case TrayCommand.OpenDownloads:
                HostLinks.Deliver(GliderLink.Downloads.Uri, LinkSource.Tray);
                break;
            case TrayCommand.CheckUpdates:
                HostApp.FireAndForget(r => r.CheckForUpdates(), "Check for updates");
                break;
            case TrayCommand.Quit:
                HostApp.FireAndForget(r => r.Quit(), "Quit", _rt.Platform.Quit);
                break;
        }
    }

    public void Dispose()
    {
        _rt.Sink.Tapped -= OnEvent;
        _tooltipTimer.Dispose();
    }
}

/// <summary>Delivers links (command line, pipe, tray) to Core once it is up. Never throws.</summary>
internal static class HostLinks
{
    public static void Deliver(string link, LinkSource source)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HostApp.Runtime.RpcTask.ConfigureAwait(false);
                if (Composition.Runtime?.Service is { } service)
                {
                    await service.HandleLinkAsync(link, source).ConfigureAwait(false);
                }
                else
                {
                    HostApp.Runtime.Platform.RestoreWindow();
                }
            }
            catch (Exception ex)
            {
                HostLog.Warn("Handling a link failed", ex);
                HostApp.Runtime.Platform.RestoreWindow();
            }
        });
    }
}
