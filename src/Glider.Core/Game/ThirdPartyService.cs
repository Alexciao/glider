using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// Games that run through another store's launcher (L§8). Two kinds, told apart by what Epic ships:
/// <list type="bullet">
/// <item><b>Managed externally</b> (<c>ThirdPartyManagedApp/Provider</c>, no Epic files): Ubisoft Connect or the EA app installs, updates
/// and starts the game. Play asks Legendary for the launch link (<c>launch --ubisoft|--origin --json</c>, the live exchange code inside)
/// and hands it to the launcher through the host. Glider can't see the game afterwards, so the session is <c>Untracked</c> and only
/// shown briefly.</item>
/// <item><b>Linked</b> (<c>partnerLinkType=ubisoft</c>, installed from Epic): launches normally, but Ubisoft must know the Epic
/// account owns it. Play returns <see cref="LaunchOutcome.NeedsActivation"/> until <c>activate --uplay</c> ran for this account.</item>
/// </list>
/// Legendary can't tell which keys are already redeemed without doing the work, so "activated" means "Glider ran it for this account".
/// EA has no separate step (the EA app asks to activate when it opens the link), so it is never gated.
/// </summary>
internal sealed class ThirdPartyService : IDisposable
{
    private const string Category = "thirdparty";

    private readonly ServiceContext _c;
    private readonly LazyState<ThirdPartyFile> _state;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _handingOff = new(StringComparer.Ordinal);

    public ThirdPartyService(ServiceContext context)
    {
        _c = context;
        _state = new LazyState<ThirdPartyFile>(Path.Combine(context.Paths.State, "thirdparty.json"), GameStateJsonContext.Default.ThirdPartyFile, ThirdPartyFile.Empty, context.Log);
    }

    /// <summary>The other store owns the install: nothing to download from Epic, Play opens its launcher.</summary>
    public static bool IsManaged(GameSummary s) =>
        s.ThirdParty != ThirdPartyStore.None && s.InstallStatus == InstallStatus.NotInstallable && s.Kind != GameKind.Dlc;

    public void Dispose() => _state.Dispose();

    private static string Name(GameSummary s) => s.ThirdPartyName ?? "its own launcher";

    private string? AccountId => _c.Hub.State.Auth.Account?.AccountId;

    private static string Key(ThirdPartyStore store, string? account, string app) => store switch
    {
        ThirdPartyStore.Ubisoft => $"ubisoft/{account}",
        _ => $"ea/{account}/{app}",
    };

    // ------------------------------------------------------------------ state

    /// <summary><see cref="GameDetail.ThirdPartyInfo"/>; null for ordinary Epic games.</summary>
    public async Task<ThirdPartyStatus?> StatusAsync(string app, CancellationToken ct)
    {
        if (_c.Library.Summary(app) is not { ThirdParty: not ThirdPartyStore.None } s)
        {
            return null;
        }

        var activated = await IsActivatedAsync(s, ct);
        return new ThirdPartyStatus(s.ThirdParty, Name(s), LauncherInstalled(s.ThirdParty), activated, NeedsActivation(s, activated), IsManaged(s));
    }

    private bool LauncherInstalled(ThirdPartyStore store)
    {
        if (store is ThirdPartyStore.Other)
        {
            return true; // no way to tell: don't claim it is missing
        }

        try
        {
            return _c.Options.ThirdPartyLaunchers.IsInstalled(store);
        }
        catch (Exception e)
        {
            _c.Log.Warn(Category, "launcher detection failed", e);
            return true;
        }
    }

    private async Task<bool> IsActivatedAsync(GameSummary s, CancellationToken ct) =>
        AccountId is { Length: > 0 } && (await _state.GetAsync(ct)).Activations.ContainsKey(Key(s.ThirdParty, AccountId, s.AppName));

    private static bool NeedsActivation(GameSummary s, bool activated) => s.ThirdParty == ThirdPartyStore.Ubisoft && !activated;

    // ------------------------------------------------------------------ activate

    /// <summary><c>game.activate</c>. Ubisoft: Legendary redeems the account's keys (metadata lane). EA: opens the EA app on the game, which asks.</summary>
    public async Task ActivateAsync(string app, CancellationToken ct)
    {
        var s = _c.Library.Summary(app) ?? throw new GliderException(Errors.NotFound(app));
        if (s.ThirdParty == ThirdPartyStore.None)
        {
            throw new GliderException(Errors.InvalidRequest($"{s.Title} doesn't run through another store's launcher."));
        }

        if (s.ThirdParty == ThirdPartyStore.Other)
        {
            throw new GliderException(Errors.ThirdPartyRequired(Name(s), s.Title, app));
        }

        await _c.RequireSignedInAsync(ct);
        if (s.ThirdParty == ThirdPartyStore.Ea)
        {
            await OpenLauncherAsync(s, offline: false, ct);
            _c.Notify(NoticeLevel.Info, $"Opened the {Name(s)}", $"Follow its prompt to activate {s.Title}.", toast: false, appName: app);
            return;
        }

        var outcome = await _c.Metadata.RunAsync(token => _c.Legendary.ActivateUplayAsync(token), ct);
        if (!outcome.Success)
        {
            var error = outcome.Error ?? Errors.ActivationFailed(Name(s), null, s.Title, app);
            throw new GliderException(error with { AppName = app });
        }

        await RecordAsync(s, ct);
        _c.Log.Info(Category, $"{app}: activated on {Name(s)}");
        _c.Notify(NoticeLevel.Success, $"{s.Title} is linked with {Name(s)}", "You can play it now.", toast: false, appName: app);
    }

    private Task RecordAsync(GameSummary s, CancellationToken ct)
    {
        var key = Key(s.ThirdParty, AccountId, s.AppName);
        var now = _c.Now;
        return AccountId is null ? Task.CompletedTask : _state.UpdateAsync(f => f with { Activations = f.Activations.SetItem(key, now) }, ct);
    }

    // ------------------------------------------------------------------ launch

    /// <summary>Linked (installed from Epic) Ubisoft games: not playable until activated for this account. Null = go on.</summary>
    public async Task<LaunchResult?> ActivationGateAsync(GameSummary s, bool offline, CancellationToken ct)
    {
        if (IsManaged(s) || s.ThirdParty != ThirdPartyStore.Ubisoft || offline || AccountId is null)
        {
            return null; // offline play can't talk to Ubisoft/Epic anyway; the game decides
        }

        if (await IsActivatedAsync(s, ct))
        {
            return null;
        }

        var e = Errors.ActivationRequired(Name(s), s.Title, s.AppName);
        _c.Log.Info(Category, $"{s.AppName}: needs activation on {Name(s)} before it can start");
        return new LaunchResult(LaunchOutcome.NeedsActivation, null, offline, e.Message);
    }

    /// <summary>A hint for a started linked game when its launcher is missing (the game would complain on its own).</summary>
    public string? LauncherNote(GameSummary s) =>
        s.ThirdParty is ThirdPartyStore.Ubisoft or ThirdPartyStore.Ea && !IsManaged(s) && !LauncherInstalled(s.ThirdParty)
            ? $"{Name(s)} isn't installed on this PC; {s.Title} may ask for it."
            : null;

    /// <summary>Play for a game managed by another store: open that store's launcher on it.</summary>
    public async Task<LaunchResult> LaunchManagedAsync(GameSummary s, LaunchRequest request, CancellationToken ct)
    {
        var app = s.AppName;
        if (s.ThirdParty == ThirdPartyStore.Other)
        {
            throw new GliderException(Errors.ThirdPartyRequired(Name(s), s.Title, app));
        }

        var mode = request.Mode ?? _c.Games.Get(app).Settings.LaunchMode;
        var offline = mode == LaunchMode.Offline || (mode == LaunchMode.Auto && _c.Hub.State.Connectivity == Connectivity.Offline);
        if (!offline)
        {
            await _c.RequireSignedInAsync(ct);
        }

        if (!LauncherInstalled(s.ThirdParty))
        {
            throw new GliderException(Errors.ThirdPartyLauncherMissing(Name(s), s.Title, app));
        }

        if (s.ThirdParty == ThirdPartyStore.Ubisoft && !offline && !await IsActivatedAsync(s, ct))
        {
            return new LaunchResult(LaunchOutcome.NeedsActivation, null, offline, Errors.ActivationRequired(Name(s), s.Title, app).Message);
        }

        await OpenLauncherAsync(s, offline, ct);
        return new LaunchResult(LaunchOutcome.ThirdPartyLauncher, null, offline, $"Opened {Name(s)}, which starts {s.Title}.");
    }

    /// <summary>Asks Legendary for the launch link and hands it to the launcher; shows an Untracked "Launching" for a few seconds.</summary>
    private async Task OpenLauncherAsync(GameSummary s, bool offline, CancellationToken ct)
    {
        var app = s.AppName;
        lock (_gate)
        {
            if (!_handingOff.Add(app))
            {
                throw new GliderException(Errors.GameRunning(s.Title, app));
            }
        }

        SetSession(app, s.Title, offline, show: true);
        try
        {
            var uri = await _c.Legendary.GetThirdPartyUriAsync(app, s.ThirdParty, offline, ct);
            var expected = s.ThirdParty == ThirdPartyStore.Ubisoft ? "uplay" : "link2ea";
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !string.Equals(parsed.Scheme, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new GliderException(Errors.LaunchFailed($"Legendary returned an unexpected link (expected {expected}://).", s.Title, app));
            }

            _c.Host.OpenThirdPartyUri(uri);
            _c.Log.Info(Category, $"{app}: handed to {Name(s)} ({expected}://, offline={offline})"); // never the link: it holds an exchange code
            await RecordAsync(s, ct);
        }
        catch
        {
            SetSession(app, s.Title, offline, show: false);
            lock (_gate)
            {
                _handingOff.Remove(app);
            }

            throw;
        }

        _ = EndHandoffAsync(app, s.Title, offline);
    }

    private async Task EndHandoffAsync(string app, string title, bool offline)
    {
        try
        {
            await Task.Delay(_c.Options.ThirdPartyHandoffDisplay, _c.Time, _c.Shutdown);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetSession(app, title, offline, show: false);
            lock (_gate)
            {
                _handingOff.Remove(app);
            }
        }
    }

    private void SetSession(string app, string title, bool offline, bool show) =>
        _c.Hub.Mutate(st =>
        {
            if (!show)
            {
                return st.Sessions.GetValueOrDefault(app) is { Untracked: true } ? st with { Sessions = st.Sessions.Remove(app) } : st;
            }

            return st with { Sessions = st.Sessions.SetItem(app, new RunningGame(app, title, RunState.Launching, null, offline, Untracked: true)) };
        });
}

/// <summary>Is another store's launcher installed on this PC? Tests fake it; the default reads the registry.</summary>
public interface IThirdPartyLaunchers
{
    bool IsInstalled(ThirdPartyStore store);
}

/// <summary>
/// A launcher is "installed" when its URL handler (<c>uplay://</c> for Ubisoft Connect, <c>link2ea://</c> for the EA app) is registered
/// and still points at an existing program: exactly what Legendary's link needs to open. Read-only.
/// </summary>
public sealed class RegistryThirdPartyLaunchers : IThirdPartyLaunchers
{
    public bool IsInstalled(ThirdPartyStore store)
    {
        var scheme = store switch
        {
            ThirdPartyStore.Ubisoft => "uplay",
            ThirdPartyStore.Ea => "link2ea",
            _ => null,
        };
        if (scheme is null || !OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{scheme}\shell\open\command");
        return key?.GetValue("") is string command && ProgramOf(command) is { } exe && File.Exists(exe);
    }

    /// <summary>The program in a shell command line (<c>"C:\Program Files\x.exe" "%1"</c> or <c>C:\x.exe %1</c>), without arguments.</summary>
    internal static string? ProgramOf(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.Length == 0)
        {
            return null;
        }

        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }

        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }
}
