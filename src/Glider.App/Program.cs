using System.Windows;
using Glider.App.Native;
using Glider.Core;
using Glider.Core.Infrastructure.Migrations;
using Glider.Core.Links;
using Microsoft.Web.WebView2.Core;
using Velopack;

namespace Glider.App;

/// <summary>Parsed command line. Unknown arguments (Velopack's --veloapp-* are consumed earlier) are ignored.</summary>
internal sealed record HostOptions(bool Background, string? MeasureFile, int MeasureSeconds, string? SelfTestDir, string? DataDir, bool LoginAudit = false, string? LoginDebugDir = null, string? Variant = null, bool NoCdp = false, string? Link = null)
{
    public static HostOptions Parse(string[] args)
    {
        bool background = false;
        string? measure = null, selftest = null, dataDir = null;
        var audit = false;
        string? loginDebug = null, variant = null;
        var noCdp = false;
        string? link = null;
        var seconds = 5;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Value() => a.Contains('=') ? a[(a.IndexOf('=') + 1)..] : (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null);
            if (a == "--background")
            {
                background = true;
            }
            else if (a.StartsWith("--launch", StringComparison.Ordinal))
            {
                // Shortcuts run "Glider.exe --launch <app>"; only a valid app name becomes a link.
                link = GliderLinkParser.LaunchUri(Value()) ?? link;
            }
            else if (a.StartsWith(GliderLinkParser.Scheme + ":", StringComparison.OrdinalIgnoreCase) || a.StartsWith(EpicLauncherUri.Scheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                link = a; // validated by Core when handled
            }
            else if (a.StartsWith("--measure-startup", StringComparison.Ordinal))
            {
                measure = Value() ?? Path.Combine(Path.GetTempPath(), "glider-startup.json");
            }
            else if (a.StartsWith("--measure-delay", StringComparison.Ordinal) && int.TryParse(Value(), out var s))
            {
                seconds = Math.Clamp(s, 1, 120);
            }
            else if (a.StartsWith("--login-debug", StringComparison.Ordinal))
            {
                loginDebug = Value() ?? Path.Combine(Path.GetTempPath(), "glider-login-debug");
            }
            else if (a == "--no-cdp")
            {
                noCdp = true;
            }
            else if (a.StartsWith("--variant", StringComparison.Ordinal))
            {
                variant = Value();
            }
            else if (a.StartsWith("--login-audit", StringComparison.Ordinal))
            {
                selftest = Value() ?? Path.Combine(Path.GetTempPath(), "glider-login-audit");
                audit = true;
            }
            else if (a.StartsWith("--selftest", StringComparison.Ordinal))
            {
                selftest = Value() ?? Path.Combine(Path.GetTempPath(), "glider-selftest");
            }
            else if (a.StartsWith("--data-dir", StringComparison.Ordinal))
            {
                dataDir = Value();
            }
        }

        return new HostOptions(background, measure, seconds, selftest, dataDir, audit, loginDebug, variant, noCdp, link);
    }

    /// <summary>A launch link starts the game without ever showing the main window.</summary>
    public bool StartsQuietly => Link is { } l
        && (GliderLinkParser.TryParse(l, out var g) ? g.Kind == GliderLinkKind.Launch
            : EpicLauncherUri.TryParse(l, out var e) && EpicLinkMapper.ToGliderLink(e)?.Kind == GliderLinkKind.Launch);

    public bool IsTestRun => MeasureFile is not null || SelfTestDir is not null || LoginDebugDir is not null;

    public LoginVariant ParsedVariant => Variant?.ToLowerInvariant() switch
    {
        "plain" => LoginVariant.Plain,
        "inprivate" => LoginVariant.InPrivate,
        "epicua" => LoginVariant.EpicUa,
        _ => LoginVariant.Legendary,
    };
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupTimeline.Begin();

        // Must be first: Velopack install/uninstall/update hooks run here and exit the process.
        VelopackApp.Build()
            .OnFirstRun(_ => ShellRegistration.OnInstalledOrUpdated())
            .OnAfterInstallFastCallback(_ => ShellRegistration.OnInstalledOrUpdated())
            .OnAfterUpdateFastCallback(_ => ShellRegistration.OnInstalledOrUpdated())
            .OnBeforeUninstallFastCallback(_ => ShellRegistration.OnUninstalling())
            .Run();
        StartupTimeline.Mark("velopack");

        var options = HostOptions.Parse(args);

        // Test runs use their own temp data dir; an explicit --data-dir gets its own single-instance scope.
        SingleInstance? single = null;
        if (!options.IsTestRun)
        {
            single = SingleInstance.TryAcquire(options.DataDir is null ? "" : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(options.DataDir.ToLowerInvariant())))[..12], options.Link);
            if (single is null)
            {
                return 0; // the first instance was asked to come to the front
            }
        }

        try
        {
            var root = options.DataDir
                ?? (options.LoginDebugDir is not null ? Path.Combine(options.LoginDebugDir, "data-" + options.ParsedVariant.ToString().ToLowerInvariant() + (options.NoCdp ? "-nocdp" : ""))
                : options.IsTestRun ? Path.Combine(Path.GetTempPath(), "glider-host-test") : null);

            // Schema 3: the per-user data leaves Velopack's install root. This must run before anything opens a file in the data
            // root (logs, WebView2, Core) and with the single-instance mutex held (always true here: root is null only when
            // this is not a test run). An explicit root is never moved.
            var prepared = DataRoot.Prepare(root, new DataRootMoverOptions { OverlayRegistry = new Glider.Core.Overlay.WindowsOverlayRegistry() });
            StartupTimeline.Mark("data-root");
            if (!prepared.CanStart)
            {
                return DataMoveFailed(prepared);
            }

            var paths = AppPaths.ForRoot(prepared.Root);
            paths.EnsureCreated();
            HostLog.Init(paths.Logs);
            foreach (var message in prepared.Messages)
            {
                HostLog.Info("data root: " + message);
            }

            if (prepared.Status == DataRootMoveStatus.Deferred)
            {
                HostLog.Warn($"data root: still using {prepared.Root} this session", prepared.Error);
            }

            return HostApp.Run(paths, options, single);
        }
        finally
        {
            single?.Dispose();
        }
    }

    /// <summary>
    /// The data is split between the old and the new root and could not be put back: starting now would run on half the data.
    /// Nothing is written in either root (a stray file there would change what the next start resumes); the details go to %TEMP%.
    /// </summary>
    private static int DataMoveFailed(DataRootMoveResult prepared)
    {
        var log = Path.Combine(Path.GetTempPath(), "glider-data-move.log");
        try
        {
            File.AppendAllLines(log, [$"{DateTimeOffset.Now:O} data move failed: {prepared.Error}", .. prepared.Messages, ""]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        Win32.MessageBox(
            0,
            $"Glider couldn't finish moving its data to {prepared.Root}, and couldn't put it back either. Nothing was lost: Glider will finish the move the next time it starts.\n\nClose any game or program that may be using Glider's files (or restart Windows), then start Glider again.\n\nDetails: {log}",
            "Glider",
            Win32.MB_ICONERROR);
        return 1;
    }
}

internal static class HostApp
{
    private static bool _fatalShown;

    public static HostRuntime Runtime { get; private set; } = null!;

    public static int Run(AppPaths paths, HostOptions options, SingleInstance? single)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // Awaits started before app.Run() (WebView2 init) must resume on the UI thread: COM objects here are STA-bound.
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        app.DispatcherUnhandledException += (_, e) =>
        {
            HostLog.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => HostLog.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            HostLog.Warn("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        var (theme, startMinimized) = HostSettingsPeek.Read(paths);
        ThemeState.Mode = theme;

        var rt = new HostRuntime(paths, app.Dispatcher, shellIntegration: !options.IsTestRun);
        Runtime = rt;
        _ = ObserveEnvironmentAsync(rt);
        rt.RpcTask.ContinueWith(t => HostLog.Error("Composition.Build failed", t.Exception), TaskContinuationOptions.OnlyOnFaulted);

        var window = new MainWindow(rt);
        app.MainWindow = window;
        var startHidden = ((options.Background || startMinimized || options.StartsQuietly) && !options.IsTestRun) || options.LoginDebugDir is not null;
        window.Begin(startHidden);

        var trayHost = new TrayHost(rt);
        var tray = new TrayIcon(
            Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"),
            open: () => rt.Platform.RestoreWindow(),
            buildMenu: trayHost.BuildMenu,
            run: trayHost.Run);
        trayHost.Attach(tray);
        rt.Platform.Attach(window, tray);
        StartupTimeline.Mark("tray");
        if (startHidden)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => MemoryTrimmer.Trim(), TaskScheduler.Default);
        }

        if (!options.IsTestRun)
        {
            AppUpdater.Start(rt.Sink);
        }

        single?.Listen(link =>
        {
            if (link is null)
            {
                HostLog.Info("activation requested by a second instance");
                app.Dispatcher.BeginInvoke(() => rt.Platform.RestoreWindow());
            }
            else
            {
                HostLinks.Deliver(link, Glider.Core.Links.LinkSource.SecondInstance);
            }
        });

        if (options.Link is { } startLink && !options.IsTestRun)
        {
            HostLinks.Deliver(startLink, Glider.Core.Links.LinkSource.CommandLine);
        }

        if (options.MeasureFile is not null)
        {
            StartupMeasure.Begin(rt, window, options.MeasureFile, options.MeasureSeconds);
        }

        if (options.LoginDebugDir is not null)
        {
            LoginDebug.Begin(rt, window, options.LoginDebugDir, options.ParsedVariant, options.NoCdp);
        }

        if (options.SelfTestDir is not null)
        {
            if (options.LoginAudit)
            {
                LoginAudit.Begin(rt, window, options.SelfTestDir);
            }
            else
            {
                SelfTest.Begin(rt, window, options.SelfTestDir);
            }
        }

        app.Run();

        trayHost.Dispose();
        tray.Dispose();
        DisposeRpc(rt);
        return 0;
    }

    internal static void FireAndForget(Func<Glider.Core.Contracts.IGliderRpc, Task> call, string what, Action? fallback = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var rpc = await Runtime.RpcTask.ConfigureAwait(false);
                await call(rpc).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HostLog.Warn($"Tray action '{what}' failed", ex);
                var message = ex is Glider.Core.Contracts.GliderException ge ? ge.Error.Message : ex.Message;
                Runtime.Platform.ShowToast(what, message);
                fallback?.Invoke();
            }
        });
    }

    private static void DisposeRpc(HostRuntime rt)
    {
        _ = rt;
        Composition.Dispose();
    }

    /// <summary>A missing WebView2 runtime is reported immediately (even when starting in the tray).</summary>
    private static async Task ObserveEnvironmentAsync(HostRuntime rt)
    {
        try
        {
            await rt.EnvTask;
            StartupTimeline.Mark("env-ready");
        }
        catch (Exception ex)
        {
            HostLog.Error("WebView2 environment creation failed", ex);
            FatalWebViewError(ex);
        }
    }

    public static void FatalWebViewError(Exception ex)
    {
        if (_fatalShown)
        {
            return;
        }

        _fatalShown = true;
        var missing = ex is WebView2RuntimeNotFoundException || ex.InnerException is WebView2RuntimeNotFoundException || ex is DllNotFoundException;
        var text = missing
            ? "Glider needs the Microsoft Edge WebView2 Runtime, which was not found on this PC.\n\nClick OK to open the download page (a small installer from Microsoft), then start Glider again."
            : "Glider couldn't start its user interface.\n\n" + ex.Message;
        var result = Win32.MessageBox(0, text, "Glider", Win32.MB_ICONERROR | (missing ? Win32.MB_OKCANCEL : 0));
        if (missing && result == (int)Win32.IDOK)
        {
            WebViewSetup.OpenExternalSafe(HostConstants.WebViewInstallLink);
        }

        Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown(1));
    }
}
