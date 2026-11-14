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
    private const string CoverFile = "store-cover.png";

    private readonly HostRuntime _rt;
    private readonly Func<MainWindow?> _window;
    private readonly DispatcherTimer _suspendTimer;
    private readonly DispatcherTimer _closeTimer;
    private readonly DispatcherTimer _publishTimer;
    private readonly DispatcherTimer _coverCleanup;
    private readonly List<StorePopupWindow> _popups = [];
    private readonly HashSet<string> _deniedHostsLogged = new(StringComparer.OrdinalIgnoreCase);

    private CoreWebView2Controller? _controller;
    private CoreWebView2? _web;
    private Task<bool>? _creating;
    private StoreBounds _bounds = new(0, 0, 0, 0);
    private string _locale = StoreLocale.FromCurrentCulture();
    private string? _currentTarget;
    private string? _lastUrl;
    private string? _glideExchangeUrl;
    private bool _wantVisible;
    private bool _windowHidden;
    private bool _hasContent;
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
        _suspendTimer.Stop();
        _closeTimer.Stop();
        _publishTimer.Stop();
        _coverCleanup.Stop();
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
        var needsNavigation = url is not null || _web!.Source is not { Length: > 0 } || _web.Source == "about:blank" || _signedOut || _phase == StorePhase.Error;
        if (needsNavigation)
        {
            _signedOut = false;
            _error = null;
            await NavigateAsync(target, bootstrap: !_bootstrapped);
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

    private Task<bool> EnsureCreatedAsync()
    {
        if (_controller is not null)
        {
            return Task.FromResult(true);
        }

        return _creating ??= CreateAsync();
    }

    private async Task<bool> CreateAsync()
    {
        try
        {
            var window = _window();
            if (window is null || window.Hwnd == 0)
            {
                return false;
            }

            Publish(StorePhase.Creating);
            var env = await _rt.EnvTask; // the single shared environment: never create another one for the same user data folder
            var options = env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = HostConstants.WebViewProfileEpic;
            options.DefaultBackgroundColor = DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
            var controller = await env.CreateCoreWebView2ControllerAsync(window.Hwnd, options);
            controller.IsVisible = false; // shown once there is something to see
            var web = controller.CoreWebView2;
            WebViewSetup.ConfigureStoreSettings(web.Settings);
            Attach(controller, web);
            _controller = controller;
            _web = web;
            _hasContent = false;
            window.Overlays?.Raise();
            HostLog.Info("store: view created");
            return true;
        }
        catch (Exception ex)
        {
            HostLog.Error("Store: creating the view failed", ex);
            _error = Errors.Unknown("The Store view couldn't be created.");
            Publish(StorePhase.Error);
            return false;
        }
        finally
        {
            _creating = null;
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
        _hasContent = false;
        _suspended = false;
        _loading = false;
        _covered = false;
        _suspendTimer.Stop();
        _closeTimer.Stop();
    }

    // ================================================================== navigation

    private async Task NavigateAsync(string url, bool bootstrap, bool forceSso = false)
    {
        if (_web is null)
        {
            return;
        }

        _currentTarget = url;
        _loading = true;
        Publish(StorePhase.Loading);

        // First open of a run (or after "try again"): make sure the Store's web session exists. No bearer cookie → one exchange-code SSO.
        var target = url;
        if (bootstrap && !_bootstrapped)
        {
            _bootstrapped = true;
            var sso = await TryBuildSsoAsync(url, forceSso);
            if (sso is not null)
            {
                _glideExchangeUrl = sso;
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
            _loading = false;
            _error = Errors.Network("The Store couldn't be opened.");
            Publish(StorePhase.Error);
        }
    }

    /// <summary>Null when the session already exists, no code is available, or the attempts are used up.</summary>
    private async Task<string?> TryBuildSsoAsync(string target, bool force)
    {
        if (_web is null || ExchangeCodeProvider is null || _ssoAttempts >= MaxSsoAttempts)
        {
            return null;
        }

        if (!force && await ProbeSignedInAsync())
        {
            return null;
        }

        string? code = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            code = await ExchangeCodeProvider(cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            HostLog.Info("store: no exchange code available");
        }

        _ssoAttempts++;
        return code is null ? null : StoreSso.Build(code, target);
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

    // ================================================================== events

    private void Attach(CoreWebView2Controller controller, CoreWebView2 web)
    {
        web.NavigationStarting += OnNavigationStarting;
        web.NavigationCompleted += OnNavigationCompleted;
        web.SourceChanged += (_, _) => SchedulePublish();
        web.HistoryChanged += (_, _) => SchedulePublish();
        web.DocumentTitleChanged += (_, _) => SchedulePublish();
        web.DOMContentLoaded += (_, _) =>
        {
            _hasContent = true;
            UpdateNativeVisibility();
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
        var fromGlide = _glideExchangeUrl is not null && e.Uri == _glideExchangeUrl;
        var decision = StoreNavigationPolicy.Decide(e.Uri, e.IsUserInitiated, CheckoutActive, fromGlide);
        switch (decision)
        {
            case StoreNavDecision.Allow:
                if (StoreNavigationPolicy.IsCheckoutUrl(e.Uri, isMainFrame: true))
                {
                    NoteCheckout();
                }

                if (fromGlide)
                {
                    _glideExchangeUrl = null; // single use: Back can never replay it
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
        _loading = false;
        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            HostLog.Warn($"store: navigation failed: {e.WebErrorStatus}");
            _hasContent = false;
            _error = e.WebErrorStatus is CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset
                or CoreWebView2WebErrorStatus.HostNameNotResolved or CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.Timeout
                ? Errors.Network("Epic's Store couldn't be reached.")
                : Errors.Unknown("The Store page couldn't be loaded.");
            UpdateNativeVisibility();
            Publish(StorePhase.Error);
            return;
        }

        if (e.IsSuccess && Uri.TryCreate(_web?.Source, UriKind.Absolute, out var u))
        {
            if (u.Host.Equals("store.epicgames.com", StringComparison.OrdinalIgnoreCase) && StoreUrls.TrySafePath(u.AbsolutePath, out var path))
            {
                _lastUrl = StoreUrls.Origin + path;
            }

            // A sign-in page: the web session is missing or expired. Try the exchange-code SSO (twice per run at most), then tell the user.
            if (IsLoginPage(u) && _ssoAttempts < MaxSsoAttempts && ExchangeCodeProvider is not null)
            {
                _bootstrapped = false;
                _ = NavigateAsync(_lastUrl ?? _currentTarget ?? StoreUrls.Home(_locale), bootstrap: true, forceSso: true);
                return;
            }

            if (IsLoginPage(u))
            {
                _signedOut = true;
                _hasContent = false;
                UpdateNativeVisibility();
                Publish(StorePhase.SignedOut);
                return;
            }
        }

        _error = null;
        UpdateNativeVisibility();
        Publish(StoreLifecycle.PhaseFor(Life));
    }

    private static bool IsLoginPage(Uri u) =>
        u.Host.EndsWith("epicgames.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.StartsWith("/id/login", StringComparison.OrdinalIgnoreCase);

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
        var shouldShow = _wantVisible && !_windowHidden && _hasContent && !_covered && !_signedOut && _error is null && window is { IsVisible: true } && !window.IsMinimized;
        SetNativeVisible(shouldShow);
        if (shouldShow)
        {
            ApplyBounds();
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
                    _window()?.Overlays?.Raise();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Store: IsVisible failed", ex);
        }
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
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
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
            CoverUrl: _covered ? $"{HostConstants.CacheUrlPrefix}{CoverFile}?v={_coverVersion}" : null,
            Error: _error);
        if (state == _published)
        {
            return;
        }

        _published = state;
        StateChanged?.Invoke(state);
    }
}
