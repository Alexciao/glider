using Glider.Core.Contracts;
using Glider.Core.Links;
using Glider.Core.Services;

namespace Glider.Core.Store;

/// <summary>
/// Core's side of the Store tab (docs/research/store-webview.md): turns typed targets into URLs for the host's native view,
/// republishes its state, routes launcher deep links into Glider, and refreshes the library after checkout activity. The view
/// itself (bounds, allowlist, timers) lives in the host behind <see cref="IStoreHost"/>.
/// </summary>
internal sealed class StoreService : IDisposable
{
    private const string Category = "store";

    private readonly ServiceContext _c;
    private readonly IEventSink _sink;
    private readonly StoreIntentRouter _router;
    private readonly CheckoutRefresher _refresher;
    private readonly LinkGate _gate = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private IStoreHost? _subscribed;
    private StoreState _last = Default;
    private string? _lastPath;
    private string _locale = StoreLocale.FromCurrentCulture();

    public StoreService(ServiceContext context, IEventSink sink, Func<LaunchRequest, CancellationToken, Task<LaunchResult>> launch)
    {
        _c = context;
        _sink = sink;
        _router = new StoreIntentRouter(new StoreIntentEnvironment
        {
            Library = () => _c.Library.Games.Values,
            RefreshLibrary = async ct =>
            {
                await _c.Library.RefreshInBackground(userInitiated: true).WaitAsync(ct).ConfigureAwait(false);
            },
            Launch = async (app, ct) =>
            {
                try
                {
                    await launch(new LaunchRequest(app, null, false), ct).ConfigureAwait(false);
                }
                catch (GliderException e)
                {
                    _c.Notify(NoticeLevel.Error, e.Error.Message, e.Error.Detail, appName: app);
                }
            },
            Navigate = route => _sink.Publish(RpcEvents.Navigate, new NavigateEvent(route)),
            Notify = (level, title, message, app) => _c.Notify(level, title, message, appName: app),
            Log = message => _c.Log.Info(Category, message),
        });
        _refresher = new CheckoutRefresher(_c.Time, RefreshAndCountNewAsync, UserPresent);
    }

    /// <summary>The state when there is no native view (not created yet, or this host has none).</summary>
    public static StoreState Default { get; } = new(false, false, false, false, null);

    /// <summary>Overrides the Store locale (tests / a future setting). Default: the UI culture.</summary>
    public string Locale
    {
        get => _locale;
        set => _locale = StoreLocale.IsKnown(value) ? value : StoreLocale.Default;
    }

    private IStoreHost? Host
    {
        get
        {
            var host = _c.Host.StoreHost;
            if (host is not null && !ReferenceEquals(host, _subscribed))
            {
                lock (_lock)
                {
                    if (!ReferenceEquals(host, _subscribed))
                    {
                        host.StateChanged += OnStateChanged;
                        host.LauncherLink += OnLauncherLink;
                        host.CheckoutActivity += OnCheckoutActivity;
                        host.WindowActivated += OnWindowActivated;
                        host.ExchangeCodeProvider = MintExchangeCodeAsync;
                        _subscribed = host;
                    }
                }
            }

            return host;
        }
    }

    private static StoreState Unavailable => Default with
    {
        Phase = StorePhase.Error,
        Error = Errors.NotAvailableYet("The Store view isn't available in this build. Use \"Open in browser\"."),
    };

    // ------------------------------------------------------------------ warm-up

    /// <summary>The Store is warmed this long after sign-in is seen, so it never competes with the first library paint.</summary>
    internal static readonly TimeSpan WarmDelay = TimeSpan.FromSeconds(6);

    private int _warmArmed;

    /// <summary>
    /// Once the account is signed in (now or later), asks the host to create its hidden Store view and load the Store a few seconds
    /// on. Fire-and-forget at low priority: a failure is logged by the host and the tab simply opens the way it always did.
    /// </summary>
    public void WarmWhenSignedIn()
    {
        void Check()
        {
            if (_c.Hub.State.Auth.Status != AuthStatus.SignedIn || Interlocked.Exchange(ref _warmArmed, 1) == 1)
            {
                return;
            }

            _c.Hub.StateChanged -= OnHub;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(WarmDelay, _c.Time, _cts.Token).ConfigureAwait(false);
                    if (Host is { } host && _c.Hub.State.Auth.Status == AuthStatus.SignedIn)
                    {
                        await host.WarmAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    _c.Log.Warn(Category, "warming the Store failed", e);
                }
            });
        }

        void OnHub(HubState _) => Check();
        _c.Hub.StateChanged += OnHub;
        Check();
    }

    // ------------------------------------------------------------------ RPCs

    public async Task<StoreState> ShowAsync(StoreShowRequest request)
    {
        if (Host is not { } host)
        {
            return Unavailable;
        }

        string? url = request.Target is null ? null : StoreUrls.Build(request.Target, _locale, _lastPath);
        var state = await host.ShowAsync(url, SanitizeBounds(request.Bounds)).ConfigureAwait(false);
        return Remember(state);
    }

    public void SetBounds(StoreBounds bounds) => Host?.SetBounds(SanitizeBounds(bounds));

    public async Task<StoreState> HideAsync()
    {
        if (Host is not { } host)
        {
            return Default;
        }

        var state = await host.HideAsync().ConfigureAwait(false);
        _ = _refresher.StoreLeftAsync(); // leaving the tab after a checkout: pick up what was bought or claimed
        return Remember(state);
    }

    public async Task<StoreState> BackAsync() => Host is { } h ? Remember(await h.BackAsync().ConfigureAwait(false)) : Default;

    public async Task<StoreState> ReloadAsync() => Host is { } h ? Remember(await h.ReloadAsync().ConfigureAwait(false)) : Default;

    public async Task<StoreState> SetCoveredAsync(StoreCoverRequest request) =>
        Host is { } h ? Remember(await h.SetCoveredAsync(request.Covered).ConfigureAwait(false)) : Default;

    public void OpenInBrowser()
    {
        if (Host is { } host)
        {
            host.OpenInBrowser();
        }
        else
        {
            _c.Host.OpenUrl(StoreUrls.Home(_locale));
        }
    }

    public StoreState GetState() => Host is { } h ? h.State : _last;

    /// <summary>Sign-out / account switch: the web session and snapshot go with it.</summary>
    public async Task DestroyAsync()
    {
        _lastPath = null;
        if (Host is { } host)
        {
            await host.DestroyAsync().ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ host events

    private void OnStateChanged(StoreState state)
    {
        var shown = Remember(state);
        if (StoreUrls.TrySafePath(PathOf(shown.Url), out var path) && shown.Host is "store.epicgames.com")
        {
            _lastPath = path;
        }

        _sink.Publish(RpcEvents.Store, shown);
    }

    private StoreState Remember(StoreState state)
    {
        lock (_lock)
        {
            _last = state;
        }

        if (state.Host == "store.epicgames.com" && StoreUrls.TrySafePath(PathOf(state.Url), out var p))
        {
            _lastPath = p;
        }

        return state;
    }

    private static string? PathOf(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : null;

    private void OnCheckoutActivity() => _refresher.CheckoutSeen();

    private void OnWindowActivated() => _ = _refresher.WindowActivatedAsync();

    private void OnLauncherLink(StoreLinkClick click)
    {
        if (!_gate.ShouldHandle(click.Uri, _c.Time.GetUtcNow()))
        {
            return; // the same link arrives through NavigationStarting, LaunchingExternalUriScheme and NewWindowRequested
        }

        if (!EpicLauncherUri.TryParse(click.Uri, out var link))
        {
            _c.Log.Info(Category, "ignored a malformed launcher link from the Store");
            return;
        }

        var trusted = click.UserInitiated && IsEpicOrigin(click.InitiatingOrigin);
        _ = Task.Run(async () =>
        {
            try
            {
                await _router.HandleAsync(link, trusted, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _c.Log.Warn(Category, "handling a launcher link failed", e);
            }
        });
    }

    /// <summary>Empty = host-initiated (Navigate()), which is trusted; otherwise the origin must be an Epic page.</summary>
    internal static bool IsEpicOrigin(string? origin) =>
        string.IsNullOrEmpty(origin) || StoreNavigationPolicy.IsEpicUrl(origin);

    // ------------------------------------------------------------------ library refresh after checkout

    private bool UserPresent() => _c.Hub.State.Auth.Status == AuthStatus.SignedIn;

    private async Task<int> RefreshAndCountNewAsync(CancellationToken ct)
    {
        var before = _c.Library.Games.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            await _c.Library.RefreshInBackground(userInitiated: false).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _c.Log.Warn(Category, "post-checkout library refresh failed", e);
            return 0;
        }

        var added = _c.Library.Games.Values
            .Where(g => !before.Contains(g.AppName) && g.Kind != GameKind.Dlc)
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var g in added)
        {
            _c.Notify(NoticeLevel.Success, $"{g.Title} was added to your library", "Open it to install.", appName: g.AppName);
        }

        return added.Count;
    }

    // ------------------------------------------------------------------ exchange code (host → Core)

    private DateTimeOffset _lastMint = DateTimeOffset.MinValue;

    /// <summary>Epic throttles the exchange call: mints are at least this far apart.</summary>
    internal static readonly TimeSpan MintSpacing = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A fresh web exchange code for the Store's single sign-on. Spaced (one per 10 s: Epic throttles this call): a mint that comes
    /// sooner waits out the gap instead of failing, so a second sign-in attempt right after the warm-up's is not a guaranteed
    /// miss. Never logged, never kept. Null when signed out, cancelled, or when Legendary/Epic refuse.
    /// </summary>
    private async Task<string?> MintExchangeCodeAsync(CancellationToken ct)
    {
        if (_c.Hub.State.Auth.Status != AuthStatus.SignedIn)
        {
            return null;
        }

        TimeSpan wait;
        lock (_lock)
        {
            var now = _c.Time.GetUtcNow();
            var earliest = _lastMint + MintSpacing;
            wait = earliest > now ? earliest - now : TimeSpan.Zero;
            _lastMint = now + wait; // reserved: a concurrent mint queues behind this one
        }

        if (wait > TimeSpan.Zero)
        {
            _c.Log.Info(Category, $"web exchange code: waiting {wait.TotalSeconds:0.#} s (Epic spaces these calls)");
            try
            {
                await Task.Delay(wait, _c.Time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        try
        {
            return await _c.Metadata.RunAsync(token => _c.Legendary.GetWebExchangeCodeAsync(token), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is GliderException or OperationCanceledException)
        {
            _c.Log.Info(Category, "no web exchange code available");
            return null;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static StoreBounds SanitizeBounds(StoreBounds b)
    {
        static double Clean(double v, double max) => double.IsFinite(v) ? Math.Clamp(v, 0, max) : 0;
        return new StoreBounds(Clean(b.X, 20000), Clean(b.Y, 20000), Clean(b.Width, 20000), Clean(b.Height, 20000));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _refresher.Dispose();
        _cts.Dispose();
    }

    /// <summary>Drops the same link seen again within two seconds (three events can report one click).</summary>
    internal sealed class LinkGate
    {
        private readonly object _l = new();
        private string? _last;
        private DateTimeOffset _at;

        public bool ShouldHandle(string uri, DateTimeOffset now)
        {
            lock (_l)
            {
                if (_last == uri && now - _at < TimeSpan.FromSeconds(2))
                {
                    return false;
                }

                _last = uri;
                _at = now;
                return true;
            }
        }
    }
}
