using System.Diagnostics;
using System.Drawing;
using System.Windows.Threading;
using Glider.App.Native;
using Glider.Core;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Links;
using Glider.Core.Store;
using Microsoft.Web.WebView2.Core;
using DrawingColor = System.Drawing.Color;

namespace Glider.App;

/// <summary>
/// The embedded Epic Games Store: a second <see cref="CoreWebView2Controller"/> on the main window's HWND, laid over the Store
/// page's HTML placeholder (docs/research/store-webview.md). Same environment and the same <c>GliderEpic</c> profile as sign-in, the
/// default Edge user agent, no bridge to the host. Created lazily, hidden + low-memory when the tab is left, suspended after 60 s,
/// closed after ~20 min without checkout activity. Every navigation goes through <see cref="StoreNavigationPolicy"/> (Core, tested).
/// All work runs on the UI thread; <see cref="IStoreHost"/> members marshal themselves.
/// </summary>
internal sealed class StoreView : IStoreHost, IDisposable
{
    private static readonly TimeSpan CheckoutWindow = TimeSpan.FromMinutes(10);
    private const int MaxSsoAttempts = 2;
    private const int MaxMintMisses = 2;

    /// <summary>How long the first open may spend getting an exchange code (Legendary queues behind a library sync) before the deadline counts the navigation.</summary>
    private static readonly TimeSpan MintBudget = TimeSpan.FromSeconds(25);
    private const string CoverFile = "store-cover.png";

    private readonly HostRuntime _rt;
    private readonly Func<MainWindow?> _window;
    private readonly DispatcherTimer _suspendTimer;
    private readonly DispatcherTimer _closeTimer;
    private readonly DispatcherTimer _publishTimer;
    private readonly DispatcherTimer _coverCleanup;
    private readonly DispatcherTimer _watchdog;
    private readonly StoreRevealTracker _reveal = new();
    private readonly Dictionary<ulong, long> _navStarted = [];
    private readonly List<StorePopupWindow> _popups = [];
    private readonly HashSet<string> _deniedHostsLogged = new(StringComparer.OrdinalIgnoreCase);

    private CoreWebView2Controller? _controller;
    private nint _hostHwnd; // the view's own child window inside the main window (WebView2 has no API for it: found by creation diff)
    private CoreWebView2? _web;
    private Task<bool>? _creating;
    private StoreBounds _bounds = new(0, 0, 0, 0);
    private string _locale = StoreLocale.FromCurrentCulture();
    private string? _currentTarget;
    private string? _lastUrl;
    private string? _glideExchangeUrl;
    private bool _wantVisible;
    private bool _windowHidden;
    private bool _contentChecked; // the revealed page has text in it (not an empty shell): the view may be shown
    private bool _warmRequested;
    private bool _warming;
    private bool _coverOk;
    private int _navGeneration;
    private int _revealGeneration;
    private int _mintMisses;
    private ulong _glideNavId;
    private bool _covered;
    private bool _loading;
    private bool _suspended;
    private bool _disposed;
    private bool _signedOut;
    private bool _bootstrapped;
    private int _ssoAttempts;
    private int _coverVersion;
    private DateTimeOffset _checkoutUntil = DateTimeOffset.MinValue;
    private StorePhase _phase = StorePhase.Idle;
    private GliderError? _error;
    private StoreState _published = Idle();

    public StoreView(HostRuntime rt, Func<MainWindow?> window)
    {
        _rt = rt;
        _window = window;
        // Bound to the UI dispatcher explicitly: Core may be the first to touch this object, from a pool thread.
        _suspendTimer = new DispatcherTimer(StoreLifecycle.SuspendAfter, DispatcherPriority.Background, (_, _) => { }, rt.Ui);
        _closeTimer = new DispatcherTimer(StoreLifecycle.CloseAfter, DispatcherPriority.Background, (_, _) => { }, rt.Ui);
        _publishTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) => { }, rt.Ui);
        _coverCleanup = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => { }, rt.Ui);
        _watchdog = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => { }, rt.Ui);
        _suspendTimer.Stop();
        _closeTimer.Stop();
        _publishTimer.Stop();
        _coverCleanup.Stop();
        _watchdog.Stop();
        _suspendTimer.Tick += async (_, _) =>
        {
            _suspendTimer.Stop();
            await SuspendAsync();
        };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            switch (StoreLifecycle.OnCloseTimer(Life))
            {
                case StoreTimerAction.Act:
                    HostLog.Info("store: closing the idle view");
                    CloseController();
                    Publish(StorePhase.Idle);
                    break;
                case StoreTimerAction.Retry:
                    _closeTimer.Start();
                    break;
            }
        };
        _publishTimer.Tick += (_, _) =>
        {
            _publishTimer.Stop();
            PublishNow();
        };
        _watchdog.Tick += (_, _) => OnWatchdog();
        _coverCleanup.Tick += (_, _) =>
        {
            _coverCleanup.Stop();
            if (!_covered)
            {
                DeleteCover();
            }
        };
    }

    public event Action<StoreState>? StateChanged;

    public event Action<StoreLinkClick>? LauncherLink;

    public event Action? CheckoutActivity;

    public event Action? WindowActivated;

    public Func<CancellationToken, Task<string?>>? ExchangeCodeProvider { get; set; }

    public StoreState State => _published;

    private Dispatcher Ui => _rt.Ui;

    private bool CheckoutActive => DateTimeOffset.UtcNow < _checkoutUntil;

    private StoreLifeState Life => new(_wantVisible, _windowHidden, _popups.Count > 0, CheckoutActive, _suspended);

    /// <summary>Self-test: the page is suspended.</summary>
    internal bool IsSuspendedForTests => _web?.IsSuspended ?? false;

    /// <summary>Self-test: how long a hidden view waits before suspending (the real value is 60 s).</summary>
    internal TimeSpan SuspendDelay
    {
        get => _suspendTimer.Interval;
        set => _suspendTimer.Interval = value;
    }

    private static StoreState Idle() => new(false, false, false, false, null);

    // ================================================================== IStoreHost (any thread)

    public Task<StoreState> ShowAsync(string? url, StoreBounds bounds) => Ui.InvokeAsync(() => ShowCoreAsync(url, bounds)).Task.Unwrap();

    public void SetBounds(StoreBounds bounds) =>
        Ui.BeginInvoke(() =>
        {
            _bounds = bounds;
            ApplyBounds();
        });

    public Task<StoreState> HideAsync() => Ui.InvokeAsync(HideCore).Task;

    public Task WarmAsync() => Ui.InvokeAsync(() => WarmCoreAsync()).Task.Unwrap();

    public Task<StoreState> BackAsync() =>
        Ui.InvokeAsync(() =>
        {
            if (_web is { CanGoBack: true } w)
            {
                w.GoBack();
            }

            return _published;
        }).Task;

    public Task<StoreState> ReloadAsync() =>
        Ui.InvokeAsync(async () =>
        {
            if (_signedOut)
            {
                _signedOut = false;
                _ssoAttempts = 0;
                _bootstrapped = false;
                _error = null;
                await NavigateAsync(_currentTarget ?? StoreUrls.Home(_locale), bootstrap: true);
            }
            else if (_web is not null)
            {
                _error = null;
                if (_phase == StorePhase.Error)
                {
                    await NavigateAsync(_currentTarget ?? StoreUrls.Home(_locale), bootstrap: false);
                }
                else
                {
                    _web.Reload();
                }
            }

            return _published;
        }).Task.Unwrap();

    public Task<StoreState> SetCoveredAsync(bool covered) => Ui.InvokeAsync(() => SetCoveredCoreAsync(covered)).Task.Unwrap();

    public void OpenInBrowser() =>
        Ui.BeginInvoke(() =>
        {
            var url = _published.Url is { } u && StoreNavigationPolicy.IsEpicUrl(u) ? u : StoreUrls.Home(_locale);
            WebViewSetup.OpenExternalSafe(url);
        });

    public Task DestroyAsync() =>
        Ui.InvokeAsync(() =>
        {
            CloseController();
            DeleteCover();
            _lastUrl = null;
            _currentTarget = null;
            _warmRequested = false;
            _mintMisses = 0;
            _bootstrapped = false;
            _signedOut = false;
            _ssoAttempts = 0;
            _checkoutUntil = DateTimeOffset.MinValue;
            Publish(StorePhase.Idle);
        }).Task;

    // ================================================================== main-window hooks (UI thread)

    /// <summary>The window was resized / moved / changed DPI: re-place the view.</summary>
    public void OnWindowMetricsChanged()
    {
        ApplyBounds();
        _controller?.NotifyParentWindowPositionChanged();
    }

    /// <summary>The window came to the foreground: Core refreshes the library if a checkout was seen.</summary>
    public void OnWindowActivated() => WindowActivated?.Invoke();

    /// <summary>
    /// Window hidden to the tray / minimized: the Store counts as hidden even if its tab is the active one (the suspended page cannot
    /// send <c>store.hide</c>): not visible, lowest memory, suspended after 60 s, closed after ~20 min without checkout activity.
    /// </summary>
    public void OnWindowHidden()
    {
        _windowHidden = true;
        if (_controller is null || _web is null)
        {
            return;
        }

        SetNativeVisible(false);
        TrySetMemory(CoreWebView2MemoryUsageTargetLevel.Low);
        ApplyTimers();
        Publish(StoreLifecycle.PhaseFor(Life));
    }

    /// <summary>Window shown again: bring the view back if its tab is the active one (re-creating it if it was closed meanwhile).</summary>
    public void OnWindowShown()
    {
        _windowHidden = false;
        ApplyBackdrop();
        if (_warmRequested && _controller is null && !_wantVisible && !_disposed)
        {
            _ = WarmCoreAsync(); // the warm-up was asked for before the window had a handle (started in the tray)
            return;
        }

        if (_disposed || !_wantVisible)
        {
            return; // another tab is active: the view stays hidden and its timers keep running
        }

        if (_controller is null)
        {
            _ = ReopenAfterIdleCloseAsync();
            return;
        }

        ApplyTimers();
        ResumeIfNeeded();
        TrySetMemory(CoreWebView2MemoryUsageTargetLevel.Normal);
        UpdateNativeVisibility();
        Publish(StoreLifecycle.PhaseFor(Life));
    }

    /// <summary>The idle timer closed the view while its tab was still open in the tray: bring it back (same page) when the window returns.</summary>
    private async Task ReopenAfterIdleCloseAsync()
    {
        try
        {
            await ShowCoreAsync(null, _bounds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: reopening after the idle close failed", ex);
        }
    }

    /// <summary>Arms the suspend and close timers while the Store is not on screen; stops them while the user is looking at it.</summary>
    private void ApplyTimers()
    {
        if (!StoreLifecycle.ShouldArmTimers(Life))
        {
            _suspendTimer.Stop();
            _closeTimer.Stop();
            return;
        }

        if (!_suspendTimer.IsEnabled)
        {
            _suspendTimer.Start();
        }

        if (!_closeTimer.IsEnabled)
        {
            _closeTimer.Start();
        }
    }

    /// <summary>Self-test: the native rectangle in physical px next to the DPI scale it was computed with.</summary>
    internal string DescribeBoundsForTests() =>
        _controller is null ? "no controller" : $"{_controller.Bounds} dpi={(_window() is { } w ? Win32.GetDpiForWindow(w.Hwnd) : 0)} visible={_controller.IsVisible}";

    /// <summary>Self-test: whether Epic's top bar is hidden in the live page.</summary>
    internal Task<string> DescribeNavForTestsAsync() =>
        _web?.ExecuteScriptAsync("(()=>{const n=document.querySelector('egs-navigation');return 'sheet='+document.adoptedStyleSheets.includes(window.__gliderNavSheet)+' nav='+(n?getComputedStyle(n).display:'none-found')+' wrapper='+(n?getComputedStyle(n.parentElement).display:'-');})()") ?? Task.FromResult("no web");

    /// <summary>Called by MainWindow before it suspends itself (tray idle): suspend the Store too.</summary>
    public Task SuspendNowAsync() => SuspendAsync();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _suspendTimer.Stop();
        _closeTimer.Stop();
        _publishTimer.Stop();
        _coverCleanup.Stop();
        _watchdog.Stop();
        CloseController();
        DeleteCover();
    }

    // ================================================================== show / hide

    private async Task<StoreState> ShowCoreAsync(string? url, StoreBounds bounds)
    {
        if (_disposed)
        {
            return _published;
        }

        _bounds = bounds;
        _wantVisible = true;
        ApplyTimers();
        _covered = false;

        if (!await EnsureCreatedAsync())
        {
            return _published;
        }

        ApplyBounds();
        ResumeIfNeeded();
        TrySetMemory(CoreWebView2MemoryUsageTargetLevel.Normal);
        _window()?.Overlays?.Raise();

        var target = url ?? _currentTarget ?? _lastUrl ?? StoreUrls.Home(_locale);
        // A warm-up (or an earlier open) is already loading this page: join it instead of starting a second chain.
        var joinsLoad = url is null && _loading && _currentTarget is not null && _phase != StorePhase.Error && !_signedOut;
        var needsNavigation = !joinsLoad && (url is not null || _web!.Source is not { Length: > 0 } || _web.Source == "about:blank" || _signedOut || _phase == StorePhase.Error);
        if (needsNavigation)
        {
            if (_phase == StorePhase.Error || _signedOut)
            {
                // "Try again" / "Sign in again": a fresh run of the sign-in, not the leftovers of the failed one.
                _bootstrapped = false;
                _ssoAttempts = 0;
                _mintMisses = 0;
            }

            _signedOut = false;
            _error = null;
            StartNavigation(target, bootstrap: !_bootstrapped);
        }
        else
        {
            UpdateNativeVisibility();
            Publish(_loading ? StorePhase.Loading : StorePhase.Ready);
        }

        try
        {
            _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: MoveFocus failed", ex);
        }

        return _published;
    }

    private StoreState HideCore()
    {
        _wantVisible = false;
        _covered = false;
        if (_controller is null)
        {
            return _published;
        }

        SetNativeVisible(false);
        TrySetMemory(CoreWebView2MemoryUsageTargetLevel.Low);
        _suspendTimer.Stop();
        _suspendTimer.Start();
        _closeTimer.Stop();
        _closeTimer.Start();
        Publish(StorePhase.Hidden);
        return _published;
    }

    private async Task SuspendAsync()
    {
        if (_web is null || _controller is null)
        {
            return;
        }

        switch (StoreLifecycle.OnSuspendTimer(Life))
        {
            case StoreTimerAction.None:
                return;
            case StoreTimerAction.Retry:
                _suspendTimer.Stop();
                _suspendTimer.Start(); // a popup or a checkout is in the way: look again later instead of never
                return;
        }

        try
        {
            SetNativeVisible(false); // TrySuspend needs IsVisible == false

            _suspended = await _web.TrySuspendAsync();
            if (_suspended)
            {
                HostLog.Info("store: suspended");
                Publish(StorePhase.Suspended);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: TrySuspend failed", ex);
        }
    }

    private void ResumeIfNeeded()
    {
        try
        {
            if (_web is { IsSuspended: true } w)
            {
                w.Resume();
            }

            _suspended = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: Resume failed", ex);
        }
    }

    // ================================================================== creation

    private Task<bool> EnsureCreatedAsync(bool quiet = false)
    {
        if (_controller is not null)
        {
            return Task.FromResult(true);
        }

        if (_creating is { } running)
        {
            return running;
        }

        // The creation clears `_creating` when it ends; started through a local first, so that an early return (no window yet) can't
        // clear it before it is assigned and leave a finished "false" cached for the rest of the run.
        var task = CreateAsync(quiet);
        _creating = task.IsCompleted ? null : task;
        return task;
    }

    private async Task<bool> CreateAsync(bool quiet)
    {
        try
        {
            var window = _window();
            if (window is null || window.Hwnd == 0)
            {
                return false;
            }

            if (!quiet)
            {
                Publish(StorePhase.Creating);
            }

            var created = Stopwatch.GetTimestamp();
            var env = await _rt.EnvTask; // the single shared environment: never create another one for the same user data folder
            var options = env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = HostConstants.WebViewProfileEpic;
            options.DefaultBackgroundColor = ViewBackground();
            var before = Win32.DirectChildren(window.Hwnd);
            var controller = await env.CreateCoreWebView2ControllerAsync(window.Hwnd, options);
            controller.IsVisible = false; // shown once there is something to see
            _hostHwnd = Win32.DirectChildren(window.Hwnd).Find(h => !before.Contains(h));
            if (_hostHwnd == 0)
            {
                HostLog.Warn("store: the view's window wasn't found; it may sit under the main page");
            }
            var web = controller.CoreWebView2;
            WebViewSetup.ConfigureStoreSettings(web.Settings);
            await web.AddScriptToExecuteOnDocumentCreatedAsync(HideEpicNavigationScript); // before the first navigation, not racing it
            await web.AddScriptToExecuteOnDocumentCreatedAsync(MicaPageScript);
            Attach(controller, web);
            _controller = controller;
            _web = web;
            _warmRequested = false;
            ResetReveal();
            window.Overlays?.Raise();
            HostLog.Info($"store: view created in {Stopwatch.GetElapsedTime(created).TotalMilliseconds:0} ms{(quiet ? " (warm-up)" : "")}");
            return true;
        }
        catch (Exception ex)
        {
            HostLog.Error("Store: creating the view failed", ex);
            if (!quiet)
            {
                _error = Errors.Unknown("The Store view couldn't be created.");
                Publish(StorePhase.Error);
            }

            return false;
        }
        finally
        {
            _creating = null;
        }
    }

    /// <summary>
    /// Warm-up: create the view hidden and load the Store, so opening the tab finds the page ready. Runs once the window exists;
    /// the hidden view is in low-memory mode, suspends after 60 s and closes after ~20 min like any hidden Store view.
    /// </summary>
    private async Task WarmCoreAsync()
    {
        _warmRequested = true;
        if (_disposed || _controller is not null || _creating is not null || _wantVisible)
        {
            return;
        }

        var window = _window();
        if (window is null || window.Hwnd == 0)
        {
            return; // no window yet (started in the tray): OnWindowShown warms it
        }

        _warming = true;
        try
        {
            if (!await EnsureCreatedAsync(quiet: true) || _disposed || _web is null)
            {
                return;
            }

            TrySetMemory(CoreWebView2MemoryUsageTargetLevel.Low);
            ApplyTimers();
            if (_wantVisible)
            {
                return; // the user opened the tab while the view was being created: ShowCore takes it from here
            }

            HostLog.Info("store: warming up in the background");
            StartNavigation(_currentTarget ?? _lastUrl ?? StoreUrls.Home(_locale), bootstrap: !_bootstrapped);
        }
        finally
        {
            _warming = false;
        }
    }

    private void CloseController()
    {
        foreach (var p in _popups.ToList())
        {
            p.Close();
        }

        _popups.Clear();
        try
        {
            _controller?.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: closing the view failed", ex);
        }

        _controller = null;
        _web = null;
        _hostHwnd = 0;
        ResetReveal();
        _navStarted.Clear();
        _navGeneration++;
        _suspended = false;
        _loading = false;
        _covered = false;
        _suspendTimer.Stop();
        _closeTimer.Stop();
        _watchdog.Stop();
    }

    // ================================================================== navigation

    /// <summary>Starts a navigation without making the caller (the <c>store.show</c> RPC, the warm-up) wait for the sign-in to be restored.</summary>
    private void StartNavigation(string url, bool bootstrap, bool forceSso = false) => _ = RunNavigationAsync(url, bootstrap, forceSso);

    private async Task RunNavigationAsync(string url, bool bootstrap, bool forceSso)
    {
        try
        {
            await NavigateAsync(url, bootstrap, forceSso);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            HostLog.Warn("Store: starting the navigation failed", ex);
            FailNavigation(new StoreRevealResult(StoreRevealEffect.Fail, StoreFailure.Other, "navigation could not start"));
        }
    }

    private async Task NavigateAsync(string url, bool bootstrap, bool forceSso = false)
    {
        if (_web is null)
        {
            return;
        }

        var generation = ++_navGeneration;
        _currentTarget = url;
        _loading = true;
        var needsSession = bootstrap && !_bootstrapped;
        _reveal.BeginHostNavigation(DateTimeOffset.UtcNow, needsSession ? MintBudget : TimeSpan.Zero);
        EnsureWatchdog();
        if (!_warming)
        {
            Publish(StorePhase.Loading);
        }

        // First open of a run (or after "try again"): make sure the Store's web session exists. No bearer cookie → one exchange-code SSO.
        var target = url;
        if (needsSession)
        {
            _bootstrapped = true;
            var sso = await TryBuildSsoAsync(url, forceSso);
            if (generation != _navGeneration || _web is null || _disposed)
            {
                return; // a newer navigation (the user opened another page, the view was closed) took over while the code was being minted
            }

            _reveal.BeginHostNavigation(DateTimeOffset.UtcNow);
            if (sso is not null)
            {
                _glideExchangeUrl = sso;
                _glideNavId = 0;
                HostLog.Info($"store: signing in to the Store ({SecretRedactor.UrlForLog(sso)})");
                target = sso;
            }
        }

        try
        {
            _web?.Navigate(target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            HostLog.Warn("Store: navigate failed", ex);
            FailNavigation(new StoreRevealResult(StoreRevealEffect.Fail, StoreFailure.Network, "navigate threw"));
        }
    }

    /// <summary>Null when the session already exists, no code is available, or the attempts are used up.</summary>
    private async Task<string?> TryBuildSsoAsync(string target, bool force)
    {
        if (_web is null || ExchangeCodeProvider is null || _ssoAttempts >= MaxSsoAttempts || _mintMisses >= MaxMintMisses)
        {
            return null;
        }

        if (!force && await ProbeSignedInAsync())
        {
            return null;
        }

        var minting = Stopwatch.GetTimestamp();
        string? code = null;
        try
        {
            using var cts = new CancellationTokenSource(MintBudget);
            code = await ExchangeCodeProvider(cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            HostLog.Info("store: no exchange code available");
        }

        HostLog.Info($"store: exchange code {(code is null ? "not available" : "minted")} after {Stopwatch.GetElapsedTime(minting).TotalMilliseconds:0} ms");
        if (code is null)
        {
            _mintMisses++; // not a used attempt, but not endless either
            return null;
        }

        _ssoAttempts++;
        return StoreSso.Build(code, target);
    }

    /// <summary>Cookie NAMES and flags only, never values: a bearer cookie that has not expired means the web session exists.</summary>
    private async Task<bool> ProbeSignedInAsync()
    {
        try
        {
            var cookies = await _web!.CookieManager.GetCookiesAsync("https://www.epicgames.com/");
            var now = DateTime.UtcNow;
            if (_ssoAttempts == 0)
            {
                HostLog.Info("store: web cookies on epicgames.com: " + string.Join(", ", cookies.Select(c => c.Name + (c.IsSession ? "(session)" : "")).Order(StringComparer.Ordinal)));
            }

            return cookies.Any(c => c.Name.Equals("EPIC_BEARER_TOKEN", StringComparison.Ordinal) && (c.IsSession || c.Expires > now));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: cookie probe failed", ex);
            return false;
        }
    }

    // ================================================================== backdrop (cosmetic only)

    /// <summary>The view's own background: transparent while the window has Mica (the page under it shows), the theme colour otherwise.</summary>
    private DrawingColor ViewBackground() =>
        _window()?.IsMicaActive == true ? DrawingColor.Transparent : DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));

    /// <summary>
    /// Puts the Store on Mica like the other tabs: the view goes transparent, and on dark theme Epic's flat page colour is dropped
    /// so Glider's content layer shows through. Purely cosmetic: only background colours change. Called by the main window on
    /// every theme, activation and backdrop change.
    /// </summary>
    public void ApplyBackdrop()
    {
        if (_controller is { } controller)
        {
            try
            {
                controller.DefaultBackgroundColor = ViewBackground();
            }
            catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or System.Runtime.InteropServices.COMException)
            {
                HostLog.Warn("store: DefaultBackgroundColor not settable", ex);
            }
        }

        PushMicaPage();
    }

    /// <summary>
    /// The field Glider's content card paints (frontend tokens.css, dark theme): on Mica the frame tint plus the thin content layer
    /// (--g-bg 12% and --g-layer 3.5% white); while Windows shows its flat fallback, the solid card colour. The Store view composes
    /// straight onto the window's Mica, not onto Glider's page, so its page paints the same field itself.
    /// </summary>
    private const string MicaField = "linear-gradient(oklch(1 0 0/.035),oklch(1 0 0/.035)),oklch(0.165 0.006 265/.12)";
    private const string SolidField = "oklch(0.2 0.007 265)";

    /// <summary>Turns the page's Mica sheet on or off (dark theme on Mica only: Epic's light text needs a dark field under it).</summary>
    private void PushMicaPage()
    {
        if (_web is not { } web)
        {
            return;
        }

        var window = _window();
        var field = window?.IsMicaActive == true && ThemeState.IsDark ? (window.IsPageTranslucent ? MicaField : SolidField) : null;
        try
        {
            _ = web.ExecuteScriptAsync($"window.__gliderMica&&window.__gliderMica({(field is null ? "null" : "'" + field + "','" + SolidField + "'")})");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // the view is going away; the next one is set up from scratch
        }
    }

    /// <summary>
    /// A constructed sheet, off until the host turns it on, in the top frame of store.epicgames.com only (never the sign-in pages,
    /// checkout frames or popups). It changes backgrounds and nothing else: the page paints Glider's content field (see
    /// <see cref="MicaField"/>) instead of Epic's flat colour, and Epic's sticky browse bar is a solid bar in that same field.
    /// </summary>
    private const string MicaPageScript =
        "(()=>{if(location.hostname!=='store.epicgames.com'||window!==window.top)return;try{const sh=new CSSStyleSheet();sh.disabled=true;" +
        "window.__gliderMica=(field,solid)=>{if(!field){sh.disabled=true;return;}" +
        "sh.replaceSync('html{background:'+field+'!important}body{background:transparent!important}nav[class^=\"_topNav_\"],nav[class*=\" _topNav_\"]{background:'+solid+'!important;-webkit-backdrop-filter:none!important;backdrop-filter:none!important;box-shadow:inset 0 -1px 0 oklch(1 0 0/.07)}');sh.disabled=false;};" +
        "const keep=()=>{if(!document.adoptedStyleSheets.includes(sh))document.adoptedStyleSheets=[...document.adoptedStyleSheets,sh];};" +
        "keep();document.addEventListener('DOMContentLoaded',keep);addEventListener('load',keep);}catch(e){}})()";

    // ================================================================== events

    /// <summary>
    /// Hides Epic's top bar on Store pages: its apps menu, Support, Distribute, language, account menu and the "Download" launcher
    /// promotion are either not needed in a launcher or duplicate Glider (the toolbar's Home button replaces the STORE logo).
    /// Its wrapper goes too (`:has`), or an empty 72 px strip stays. The Store's own search/browse row is untouched.
    /// </summary>
    private const string HideEpicNavigationScript =
        "(()=>{if(location.hostname!=='store.epicgames.com')return;const css='egs-navigation,:has(> egs-navigation){display:none!important}';" +
        // A constructed sheet is not a node in <head>, so the page's hydration (which rewrites <head>) can't drop it.
        "try{const sh=new CSSStyleSheet();sh.replaceSync(css);window.__gliderNavSheet=sh;" +
        "const keep=()=>{if(!document.adoptedStyleSheets.includes(sh))document.adoptedStyleSheets=[...document.adoptedStyleSheets,sh];};" +
        "keep();document.addEventListener('DOMContentLoaded',keep);addEventListener('load',keep);}catch(e){" +
        "const add=()=>{if(document.getElementById('glider-hide-nav'))return;const s=document.createElement('style');s.id='glider-hide-nav';s.textContent=css;" +
        "(document.head||document.documentElement).appendChild(s);};add();document.addEventListener('DOMContentLoaded',add);}})()";

    private void Attach(CoreWebView2Controller controller, CoreWebView2 web)
    {
        web.NavigationStarting += OnNavigationStarting;
        web.NavigationCompleted += OnNavigationCompleted;
        web.SourceChanged += (_, _) => SchedulePublish();
        web.HistoryChanged += (_, _) => SchedulePublish();
        web.DocumentTitleChanged += (_, _) => SchedulePublish();
        web.DOMContentLoaded += (_, _) =>
        {
            PushMicaPage();
            _reveal.OnDomContentLoaded(_web?.Source, DateTimeOffset.UtcNow);
        };
        web.FrameNavigationStarting += OnFrameNavigationStarting;
        web.LaunchingExternalUriScheme += OnLaunchingExternalUri;
        web.NewWindowRequested += OnNewWindowRequested;
        web.DownloadStarting += OnDownloadStarting;
        web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        web.ServerCertificateErrorDetected += (_, e) =>
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel; // no click-through of TLS errors
            HostLog.Warn("store: TLS error on a navigation; blocked");
        };
        web.ProcessFailed += OnProcessFailed;
        controller.AcceleratorKeyPressed += OnAcceleratorKey;
        controller.MoveFocusRequested += (_, e) =>
        {
            _window()?.FocusUi(e.Reason == CoreWebView2MoveFocusReason.Previous);
            e.Handled = true;
        };
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // The exchange URL is ours when it matches the one the host started, or when it is a later hop of that same navigation.
        var fromGlide = _glideExchangeUrl is not null
            && (StoreSso.IsSameExchange(e.Uri, _glideExchangeUrl) || (_glideNavId == e.NavigationId && StoreSso.IsExchangeUrl(e.Uri)));
        var decision = StoreNavigationPolicy.Decide(e.Uri, e.IsUserInitiated, CheckoutActive, fromGlide);
        if (e.Uri is not "about:blank")
        {
            HostLog.Info("store: " + StoreNavLog.Starting(e.Uri, decision, e.IsRedirected, e.IsUserInitiated));
        }

        _navStarted.TryAdd(e.NavigationId, Stopwatch.GetTimestamp());
        _reveal.OnNavigationStarting(e.NavigationId, decision, DateTimeOffset.UtcNow);
        EnsureWatchdog();
        switch (decision)
        {
            case StoreNavDecision.Allow:
                if (StoreNavigationPolicy.IsCheckoutUrl(e.Uri, isMainFrame: true))
                {
                    NoteCheckout();
                }

                if (fromGlide)
                {
                    _glideNavId = e.NavigationId; // its redirect hops are the host's too; the URL is forgotten when the chain ends (single use: Back can never replay it)
                }

                _loading = true;
                SchedulePublish();
                break;
            case StoreNavDecision.LauncherLink:
                e.Cancel = true;
                RaiseLauncherLink(e.Uri, e.IsUserInitiated, OriginOfCurrentPage());
                break;
            case StoreNavDecision.OpenInSystemBrowser:
                e.Cancel = true;
                WebViewSetup.OpenExternalSafe(e.Uri);
                break;
            default:
                e.Cancel = true;
                LogDeniedOnce(e.Uri);
                break;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var url = _web?.Source;
        var elapsed = _navStarted.Remove(e.NavigationId, out var started) ? Stopwatch.GetElapsedTime(started) : TimeSpan.Zero;
        var cancelled = e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled;
        HostLog.Info("store: " + StoreNavLog.Completed(url, e.IsSuccess, e.WebErrorStatus.ToString(), e.HttpStatusCode, elapsed));
        var network = e.WebErrorStatus is CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset
            or CoreWebView2WebErrorStatus.HostNameNotResolved or CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.Timeout;
        var result = _reveal.OnNavigationCompleted(e.NavigationId, e.IsSuccess, cancelled, network, url, DateTimeOffset.UtcNow);
        _loading = _reveal.Pending;

        if (e.IsSuccess && Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            if (u.Host.Equals("store.epicgames.com", StringComparison.OrdinalIgnoreCase) && StoreUrls.TrySafePath(u.AbsolutePath, out var path))
            {
                _lastUrl = StoreUrls.Origin + path;
            }

            if (!StoreSso.IsExchangeUrl(url) && _glideNavId == e.NavigationId)
            {
                _glideExchangeUrl = null; // the sign-in hop is over: a replay (Back) is cancelled again
                _glideNavId = 0;
            }
        }

        ApplyReveal(result);
    }

    /// <summary>Does what <see cref="StoreRevealTracker"/> decided: show the page, run the sign-in, or show the error.</summary>
    private void ApplyReveal(StoreRevealResult result)
    {
        switch (result.Effect)
        {
            case StoreRevealEffect.Reveal:
                HostLog.Info($"store: page ready ({result.Reason})");
                BeginReveal();
                break;
            case StoreRevealEffect.LoginPage:
                _contentChecked = false;
                _revealGeneration++;
                HostLog.Info("store: landed on the sign-in page");
                if (_ssoAttempts < MaxSsoAttempts && _mintMisses < MaxMintMisses && ExchangeCodeProvider is not null)
                {
                    // The web session is missing or expired: one more exchange-code SSO (twice per run at most), then tell the user.
                    _bootstrapped = false;
                    StartNavigation(_lastUrl ?? _currentTarget ?? StoreUrls.Home(_locale), bootstrap: true, forceSso: true);
                    return;
                }

                _signedOut = true;
                _loading = false;
                UpdateNativeVisibility();
                Publish(StorePhase.SignedOut);
                break;
            case StoreRevealEffect.Fail:
                FailNavigation(result);
                break;
            default:
                UpdateNativeVisibility();
                break;
        }
    }

    private void FailNavigation(StoreRevealResult result)
    {
        HostLog.Warn($"store: navigation failed: {result.Failure} ({result.Reason}) at {StoreNavLog.Describe(_web?.Source)}");
        _loading = false;
        ResetReveal();
        _error = result.Failure switch
        {
            StoreFailure.Network => Errors.Network("Epic's Store couldn't be reached."),
            StoreFailure.Timeout => Errors.Network("The Store is taking too long to open."),
            StoreFailure.Blocked => Errors.Unknown("The Store tried to open a page Glider doesn't allow here."),
            _ => Errors.Unknown("The Store page couldn't be loaded."),
        };
        UpdateNativeVisibility();
        Publish(StorePhase.Error);
    }

    // ------------------------------------------------------------------ reveal

    private void ResetReveal()
    {
        _reveal.Reset();
        _contentChecked = false;
        _revealGeneration++;
    }

    /// <summary>
    /// The final page loaded. Before it is shown, a last look inside: Epic's pages are script-built, so a "loaded" shell can still be
    /// an empty document for a second or two (a dark rectangle that reads as a broken tab). The text length is the only thing read.
    /// </summary>
    private void BeginReveal()
    {
        var generation = ++_revealGeneration;
        _error = null;
        _ = ShowWhenFilledAsync(generation);
    }

    private async Task ShowWhenFilledAsync(int generation)
    {
        try
        {
            if (_wantVisible && !_windowHidden && _web is { IsSuspended: false } web)
            {
                for (var i = 0; i < 12 && generation == _revealGeneration; i++)
                {
                    if (await PageHasTextAsync(web))
                    {
                        break;
                    }

                    await Task.Delay(250);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: content check failed", ex);
        }

        if (generation != _revealGeneration || _disposed || _web is null)
        {
            return;
        }

        _contentChecked = true;
        _error = null;
        UpdateNativeVisibility();
        Publish(StoreLifecycle.PhaseFor(Life));
    }

    private static async Task<bool> PageHasTextAsync(CoreWebView2 web)
    {
        var json = await web.ExecuteScriptAsync("document.body ? document.body.innerText.length : 0");
        return !int.TryParse(json, out var length) || length > 40;
    }

    private void EnsureWatchdog()
    {
        if (!_watchdog.IsEnabled)
        {
            _watchdog.Start();
        }
    }

    private void OnWatchdog()
    {
        if (!_reveal.Pending)
        {
            _watchdog.Stop();
            return;
        }

        ApplyReveal(_reveal.Tick(DateTimeOffset.UtcNow));
        if (!_reveal.Pending)
        {
            _watchdog.Stop();
        }
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (StoreNavigationPolicy.IsLauncherLink(e.Uri))
        {
            e.Cancel = true;
            RaiseLauncherLink(e.Uri, e.IsUserInitiated, OriginOfCurrentPage());
            return;
        }

        if (!StoreNavigationPolicy.AllowFrame(e.Uri))
        {
            e.Cancel = true;
            return;
        }

        if (StoreNavigationPolicy.IsCheckoutUrl(e.Uri, isMainFrame: false))
        {
            NoteCheckout();
        }
    }

    private void OnLaunchingExternalUri(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true; // nothing of the Store may start another program; launcher links are handled by Glider itself
        if (StoreNavigationPolicy.IsLauncherLink(e.Uri))
        {
            RaiseLauncherLink(e.Uri, e.IsUserInitiated, e.InitiatingOrigin);
        }
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var decision = StoreNavigationPolicy.DecidePopup(e.Uri, e.IsUserInitiated, _web?.Source, CheckoutActive, _popups.Count);
        switch (decision)
        {
            case StorePopupDecision.Block:
                e.Handled = true;
                break;
            case StorePopupDecision.NavigateInPlace:
                e.Handled = true;
                _web?.Navigate(e.Uri); // goes through NavigationStarting like any other navigation
                break;
            case StorePopupDecision.LauncherLink:
                e.Handled = true;
                RaiseLauncherLink(e.Uri, e.IsUserInitiated, OriginOfCurrentPage());
                break;
            case StorePopupDecision.OpenInSystemBrowser:
                e.Handled = true;
                WebViewSetup.OpenExternalSafe(e.Uri);
                break;
            case StorePopupDecision.Popup:
                await OpenPopupAsync(e);
                break;
        }
    }

    private async Task OpenPopupAsync(CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var owner = _window();
            var env = await _rt.EnvTask;
            if (owner is null)
            {
                e.Handled = true;
                return;
            }

            var popup = new StorePopupWindow(env, owner);
            _popups.Add(popup);
            popup.Closed += (_, _) => _popups.Remove(popup);
            await popup.InitAsync();
            e.NewWindow = popup.WebView;
            e.Handled = true;
            HostLog.Info("store: opened a login/payment popup");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            HostLog.Warn("Store: opening a popup failed", ex);
            e.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true; // e.g. the "Get the launcher" installer: Glider replaces it
        HostLog.Info("store: a download was blocked");
        _rt.Platform.ShowToast("Glider replaces the Epic Games Launcher", "No installer download is needed.");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        HostLog.Warn($"store: process failed: {e.ProcessFailedKind}");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                Ui.BeginInvoke(() => _web?.Reload());
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                Ui.BeginInvoke(() =>
                {
                    CloseController();
                    Publish(StorePhase.Idle);
                });
                break;
        }
    }

    private void OnAcceleratorKey(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown && e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown)
        {
            return;
        }

        var alt = (e.KeyEventLParam & (1 << 29)) != 0;
        var ctrl = (Win32.GetKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
        switch (e.VirtualKey)
        {
            case 0x25 when alt: // Alt+Left
                if (_web is { CanGoBack: true })
                {
                    _web.GoBack();
                    e.Handled = true;
                }

                break;
            case 0x27 when alt: // Alt+Right
                if (_web is { CanGoForward: true })
                {
                    _web.GoForward();
                    e.Handled = true;
                }

                break;
            case 0x74: // F5
            case 0x52 when ctrl: // Ctrl+R
                _web?.Reload();
                e.Handled = true;
                break;
        }
    }

    // ================================================================== checkout / links

    private void NoteCheckout()
    {
        var first = !CheckoutActive;
        _checkoutUntil = DateTimeOffset.UtcNow + CheckoutWindow;
        if (first)
        {
            HostLog.Info("store: checkout activity");
        }

        CheckoutActivity?.Invoke();
        SchedulePublish();
    }

    private void RaiseLauncherLink(string uri, bool userInitiated, string? origin)
    {
        // Never log the link itself (it may carry arguments); Core parses it and drops everything but the whitelist.
        HostLog.Info("store: launcher link from the page");
        LauncherLink?.Invoke(new StoreLinkClick(uri, userInitiated, origin));
    }

    private string? OriginOfCurrentPage() =>
        Uri.TryCreate(_web?.Source, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null;

    private void LogDeniedOnce(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && _deniedHostsLogged.Add(u.Scheme + "://" + u.Host))
        {
            HostLog.Info($"store: blocked a navigation to {u.Scheme}://{u.Host}");
        }
    }

    // ================================================================== bounds / visibility / cover

    private void ApplyBounds()
    {
        var window = _window();
        if (_controller is null || window is null || window.Hwnd == 0 || !Win32.GetClientRect(window.Hwnd, out var client))
        {
            return;
        }

        var scale = Win32.GetDpiForWindow(window.Hwnd) / 96.0;
        var x = (int)Math.Round(_bounds.X * scale);
        var y = (int)Math.Round(_bounds.Y * scale);
        var w = (int)Math.Round(_bounds.Width * scale);
        var h = (int)Math.Round(_bounds.Height * scale);
        // Clamp inside the client area (resize races, maximize animation).
        x = Math.Clamp(x, 0, Math.Max(0, client.Width));
        y = Math.Clamp(y, 0, Math.Max(0, client.Height));
        w = Math.Clamp(w, 0, Math.Max(0, client.Width - x));
        h = Math.Clamp(h, 0, Math.Max(0, client.Height - y));
        try
        {
            _controller.Bounds = new Rectangle(x, y, w, h);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: setting bounds failed", ex);
        }
    }

    /// <summary>On screen = the tab is active, there is a page to show, nothing is covering it, and the window is up.</summary>
    private void UpdateNativeVisibility()
    {
        var window = _window();
        var shouldShow = _wantVisible && !_windowHidden && _reveal.Revealed && _contentChecked && !_covered && !_signedOut && _error is null && window is { IsVisible: true } && !window.IsMinimized;
        SetNativeVisible(shouldShow);
        if (shouldShow)
        {
            ApplyBounds();
            RaiseAboveMainPage();
        }

        SchedulePublish();
    }

    private void SetNativeVisible(bool visible)
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            if (_controller.IsVisible != visible)
            {
                _controller.IsVisible = visible;
                if (visible)
                {
                    _controller.NotifyParentWindowPositionChanged();
                    RaiseAboveMainPage();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: IsVisible failed", ex);
        }
    }

    /// <summary>
    /// The view's window is a sibling of the main page's, and it was created *under* it (WebView2 places a new controller's window
    /// at the bottom): the Store loaded and rendered, but behind the page, so the tab showed an empty dark area. Put it right
    /// under the caption overlays whenever it is shown.
    /// </summary>
    private void RaiseAboveMainPage()
    {
        if (_hostHwnd != 0)
        {
            Win32.SetWindowPos(_hostHwnd, Win32.HWND_TOP, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }

        _window()?.Overlays?.Raise();
    }

    private void TrySetMemory(CoreWebView2MemoryUsageTargetLevel level)
    {
        try
        {
            if (_web is not null)
            {
                _web.MemoryUsageTargetLevel = level;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: memory level failed", ex);
        }
    }

    private async Task<StoreState> SetCoveredCoreAsync(bool covered)
    {
        if (_controller is null || _web is null || _covered == covered)
        {
            return _published;
        }

        HostLog.Info(covered ? "store: covered by an overlay in the UI (snapshot shown)" : "store: uncovered");
        if (covered)
        {
            // A snapshot stands in for the live page while an HTML dialog sits above it.
            try
            {
                Directory.CreateDirectory(_rt.Paths.Cache);
                var path = Path.Combine(_rt.Paths.Cache, CoverFile);
                await using (var fs = File.Create(path))
                {
                    await _web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                }

                _coverVersion++;
                _coverOk = true;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                _coverOk = false; // no stale or half-written picture is handed to the placeholder
                HostLog.Warn("Store: cover snapshot failed", ex);
            }

            _covered = true;
            _coverCleanup.Stop();
        }
        else
        {
            _covered = false;
            _coverCleanup.Start();
        }

        UpdateNativeVisibility();
        PublishNow();
        return _published;
    }

    private void DeleteCover()
    {
        AtomicFile.TryDelete(Path.Combine(_rt.Paths.Cache, CoverFile));
    }

    // ================================================================== state

    private void Publish(StorePhase phase)
    {
        _phase = phase;
        PublishNow();
    }

    private void SchedulePublish()
    {
        if (!_publishTimer.IsEnabled)
        {
            _publishTimer.Start(); // ~10 events/s at most
        }
    }

    private void PublishNow()
    {
        _publishTimer.Stop();
        var web = _web;
        string? url = null, host = null, title = null;
        var secure = false;
        if (web is not null && Uri.TryCreate(web.Source, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
        {
            url = StoreUrls.Sanitize(web.Source);
            host = u.Host;
            secure = true;
        }

        if (web is not null && !string.IsNullOrWhiteSpace(web.DocumentTitle))
        {
            title = web.DocumentTitle.Length > 120 ? web.DocumentTitle[..120] : web.DocumentTitle;
        }

        var onScreen = _controller is { IsVisible: true };
        var state = new StoreState(
            Created: _controller is not null,
            Visible: onScreen,
            Loading: _loading,
            CanGoBack: web?.CanGoBack ?? false,
            Title: title,
            Phase: _phase,
            Url: url,
            Host: host,
            Secure: secure,
            CanGoForward: web?.CanGoForward ?? false,
            CheckoutActive: CheckoutActive,
            Covered: _covered,
            CoverUrl: _covered && _coverOk ? $"{HostConstants.CacheUrlPrefix}{CoverFile}?v={_coverVersion}" : null,
            Error: _error);
        if (state == _published)
        {
            return;
        }

        _published = state;
        StateChanged?.Invoke(state);
    }
}
