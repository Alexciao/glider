using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Links;

public enum LinkSource
{
    /// <summary>The first Glider process was started with a link (protocol handler, shortcut, double-click).</summary>
    CommandLine,
    /// <summary>A second Glider process forwarded the link through the single-instance pipe.</summary>
    SecondInstance,
    /// <summary>The tray menu (Open Downloads).</summary>
    Tray,
}

public enum LinkOutcome
{
    /// <summary>The link was acted on (a game launched, a page requested).</summary>
    Handled,
    /// <summary>Refused or unsupported; the main window was shown so the user is not left with an invisible process.</summary>
    Ignored,
}

/// <summary>
/// Turns a <c>glider://</c> or <c>com.epicgames.launcher://</c> link into an action. Launch links start the game without ever showing
/// the window; everything else shows the window and sends the UI a `navigate` route. Links never install anything: an install link only
/// opens the game's install dialog.
/// </summary>
internal sealed class LinkService
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(1.5);

    private readonly ServiceContext _c;
    private readonly Func<LaunchRequest, CancellationToken, Task<LaunchResult>> _launch;
    private readonly Lock _gate = new();
    private string? _pendingRoute;
    private bool _uiReady;
    private string? _lastUri;
    private DateTimeOffset _lastAt;

    public LinkService(ServiceContext context, Func<LaunchRequest, CancellationToken, Task<LaunchResult>> launch)
    {
        _c = context;
        _launch = launch;
    }

    private IShellIntegration? Shell => _c.Options.Shell;

    // ---------------------------------------------------------------- routes to the UI

    /// <summary>The UI called app.ready: it can receive `navigate` now. A route requested during start-up is delivered first.</summary>
    public void UiReady()
    {
        string? route;
        lock (_gate)
        {
            _uiReady = true;
            route = _pendingRoute;
            _pendingRoute = null;
        }

        if (route is not null)
        {
            _c.Hub.Sink.Publish(RpcEvents.Navigate, new NavigateEvent(route));
        }
    }

    private void Navigate(string route)
    {
        lock (_gate)
        {
            if (!_uiReady)
            {
                _pendingRoute = route; // latest wins: the user only sees the last link they clicked
                return;
            }
        }

        _c.Hub.Sink.Publish(RpcEvents.Navigate, new NavigateEvent(route));
    }

    private void Show(string route)
    {
        Navigate(route);
        _c.Host.RestoreWindow();
    }

    // ---------------------------------------------------------------- handling

    public async Task<LinkOutcome> HandleAsync(string? raw, LinkSource source, CancellationToken ct)
    {
        var link = Parse(raw, out var reason);
        if (link is null)
        {
            _c.Log.Warn("links", $"ignored a link from {source}: {reason}");
            _c.Host.RestoreWindow();
            return LinkOutcome.Ignored;
        }

        if (IsDuplicate(link))
        {
            return LinkOutcome.Handled;
        }

        _c.Log.Info("links", $"{source}: {link.Uri}");
        if (link.AppName is null)
        {
            Show(link.Route);
            return LinkOutcome.Handled;
        }

        var game = Find(link.AppName, epicNamespace: null, epicCatalogItemId: null);
        if (game is null)
        {
            ShowNotInLibrary(link.AppName);
            return LinkOutcome.Handled;
        }

        link = link.WithApp(game.AppName);
        switch (link.Kind)
        {
            case GliderLinkKind.Launch:
                await LaunchAsync(game, ct).ConfigureAwait(false);
                break;
            case GliderLinkKind.Install:
                Show(game.InstallStatus is InstallStatus.Installed or InstallStatus.Broken ? GliderLink.Game(game.AppName).Route : link.Route);
                break;
            default:
                Show(link.Route);
                break;
        }

        return LinkOutcome.Handled;
    }

    private GliderLink? Parse(string? raw, out string reason)
    {
        reason = "";
        var text = raw?.Trim().Trim('"') ?? "";
        if (text.StartsWith(GliderLinkParser.Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            var why = GliderLinkParser.Parse(text, out var link);
            reason = why.ToString();
            return why == LinkRejection.None ? link : null;
        }

        if (text.StartsWith(EpicLauncherUri.Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            if (!_c.Settings.HandleEpicLinks)
            {
                reason = "Epic launcher links are not handled (setting is off)";
                return null;
            }

            var why = EpicLauncherUri.Parse(text, out var epic);
            if (why != LinkRejection.None)
            {
                reason = why.ToString();
                return null;
            }

            var mapped = EpicLinkMapper.ToGliderLink(epic);
            if (mapped is null)
            {
                reason = $"nothing to do for Epic link kind {epic.Kind}, action {epic.Action}";
                return null;
            }

            // The app part of an Epic link may only be known by (namespace, catalog item): resolve it against the library now.
            if (epic is { Kind: EpicLinkKind.App, AppName: { } app })
            {
                var game = Find(app, epic.Namespace, epic.CatalogItemId);
                return game is null ? mapped : mapped.WithApp(game.AppName);
            }

            return mapped;
        }

        reason = LinkRejection.BadScheme.ToString();
        return null;
    }

    private bool IsDuplicate(GliderLink link)
    {
        var now = _c.Now;
        lock (_gate)
        {
            if (_lastUri == link.Uri && now - _lastAt < DuplicateWindow)
            {
                return true;
            }

            _lastUri = link.Uri;
            _lastAt = now;
            return false;
        }
    }

    private GameSummary? Find(string app, string? epicNamespace, string? epicCatalogItemId)
    {
        var games = _c.Library.Games;
        if (games.TryGetValue(app, out var exact))
        {
            return exact;
        }

        foreach (var g in games.Values)
        {
            if (string.Equals(g.AppName, app, StringComparison.OrdinalIgnoreCase))
            {
                return g;
            }
        }

        if (epicNamespace is not null && epicCatalogItemId is not null)
        {
            foreach (var g in games.Values)
            {
                if (g.Kind != GameKind.Dlc && g.Namespace == epicNamespace && g.CatalogItemId == epicCatalogItemId)
                {
                    return g;
                }
            }
        }

        return null;
    }

    private void ShowNotInLibrary(string app)
    {
        _c.Log.Info("links", $"{app} is not in the library");
        _c.Host.ShowToast("Game not in your library", $"{app} isn't in your Glider library yet. Open Library and refresh if you just got it.");
        Show(GliderLink.Library.Route);
    }

    // ---------------------------------------------------------------- launch

    private async Task LaunchAsync(GameSummary game, CancellationToken ct)
    {
        if (game.Kind == GameKind.Dlc)
        {
            Show(GliderLink.Game(game.BaseAppName ?? game.AppName).Route);
            return;
        }

        switch (game.InstallStatus)
        {
            case InstallStatus.NotInstalled:
                Show(GliderLink.Install(game.AppName).Route); // the dialog, never a silent download
                return;
            case InstallStatus.Broken:
                Show(GliderLink.Game(game.AppName).Route);
                return;
        }

        try
        {
            var result = await _launch(new LaunchRequest(game.AppName, null, false), ct).ConfigureAwait(false);
            if (result.Outcome is LaunchOutcome.SaveConflict or LaunchOutcome.NeedsActivation)
            {
                Show(GliderLink.Game(game.AppName).Route); // the user has to decide: cloud saves conflict, store activation
            }
        }
        catch (GliderException e) when (e.Error.Code == ErrorCode.GameRunning)
        {
            _c.Host.ShowToast(game.Title, "Already running.");
        }
        catch (GliderException e)
        {
            _c.Log.Warn("links", $"launching {game.AppName} from a link failed: {e.Error.Message}");
            _c.Host.ShowToast(e.Error.Title, e.Error.Message);
            Show(GliderLink.Game(game.AppName).Route);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    // ---------------------------------------------------------------- protocol registration

    private ProtocolRegistrar? Registrar() => Shell is { } shell
        ? new ProtocolRegistrar(shell.Registry, shell.LaunchExePath, Path.Combine(_c.Paths.State, "protocol-backup.json"), message => _c.Log.Info("links", message))
        : null;

    /// <summary>Settings.HandleEpicLinks changed: take the Epic scheme over (backing up the current handler) or give it back.</summary>
    public void ApplyEpicHandler(bool enable)
    {
        var registrar = Registrar();
        if (registrar is null)
        {
            _c.Log.Info("links", "no shell integration in this run; the Epic link handler setting only changes the stored value");
            return;
        }

        try
        {
            var status = enable ? registrar.EnableEpicHandler() : registrar.DisableEpicHandler();
            if (enable && status.UserChoiceProgId is { } choice)
            {
                _c.Log.Warn("links", $"Windows has a default-app choice for Epic links ({choice}); it may override Glider's registration");
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _c.Log.Warn("links", "could not change the Epic link handler", e);
            throw new GliderException(Errors.PermissionDenied("Windows didn't let Glider change the Epic link handler: " + e.Message), e);
        }
    }

    /// <summary>At start-up: keep glider:// pointing at this install (installed builds) and the Epic scheme in step with the setting.</summary>
    public void Reconcile()
    {
        var registrar = Registrar();
        if (registrar is null)
        {
            return;
        }

        try
        {
            if (Shell!.ManagesGliderScheme)
            {
                registrar.EnsureGliderScheme();
            }

            if (_c.Settings.HandleEpicLinks)
            {
                registrar.EnableEpicHandler();
            }
            else if (registrar.GetEpicStatus() is { GliderIsHandler: true } or { BackupHeld: true })
            {
                registrar.DisableEpicHandler(); // the setting is off but our key (or a stale backup) is still there
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _c.Log.Warn("links", "protocol registration check failed", e);
        }
    }
}
