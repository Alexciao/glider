using System.Windows.Threading;
using Glider.Core;
using Glider.Core.Contracts;
using Microsoft.Web.WebView2.Core;

namespace Glider.App;

/// <summary>Process-wide wiring: paths, UI dispatcher, WebView2 environment, bridge, event sink, platform and the RPC service.</summary>
internal sealed class HostRuntime
{
    /// <param name="shellIntegration">Shortcuts and URL protocols touch the user's Desktop, Start menu and registry; test runs pass false.</param>
    public HostRuntime(AppPaths paths, Dispatcher ui, bool shellIntegration = true)
    {
        Paths = paths;
        Ui = ui;
        Bridge = new WebViewBridge(ui);
        Sink = new ForwardingEventSink();
        Platform = new WindowsHostPlatform(this);

        // 0) The RPC route table (reflection over IGliderRpc + every contract type's JSON metadata) is built on a pool thread at once.
        _ = Task.Run(() => RpcDispatcher.MethodNames.Count);

        // 1) Browser-process startup is the long pole, so it starts first and overlaps everything below.
        EnvTask = StartEnvironment(paths);

        // 2) Core's service layer is built on the thread pool, in parallel with window construction.
        RpcTask = Task.Run(() => Composition.BuildAsync(paths, Platform, Sink, shellIntegration ? new WindowsShellIntegration() : null));
        Bridge.Dispatcher = RpcTask.ContinueWith(
            t =>
            {
                var dispatcher = new RpcDispatcher(t.Result, Bridge, (method, ex) => HostLog.Error($"RPC {method} failed", ex), OnRpcHandled);
                Sink.Attach(dispatcher);
                StartupTimeline.Mark("rpc-ready");
                return dispatcher;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    /// <summary>Startup diagnostics: a milestone when each startup-critical response left Core, and a log line for anything slow.</summary>
    private static void OnRpcHandled(string method, double ms, int chars)
    {
        if (method is "app.getSnapshot" or "library.get")
        {
            StartupTimeline.Mark($"rpc-sent:{method}");
            HostLog.Info($"RPC {method}: {ms:0.0} ms in Core, {chars / 1024} KiB");
        }
        else if (ms > 250)
        {
            HostLog.Warn($"RPC {method} took {ms:0} ms");
        }
    }

    public AppPaths Paths { get; }

    public Dispatcher Ui { get; }

    public WebViewBridge Bridge { get; }

    public ForwardingEventSink Sink { get; }

    public WindowsHostPlatform Platform { get; }

    public Task<CoreWebView2Environment> EnvTask { get; private set; }

    /// <summary>After a WebView2 browser-process crash the old environment is dead: a new controller needs a freshly created one.</summary>
    public void RecreateEnvironment() => EnvTask = StartEnvironment(Paths);

    public Task<IGliderRpc> RpcTask { get; }

    public IGliderRpc? Rpc => RpcTask.IsCompletedSuccessfully ? RpcTask.Result : null;

    /// <summary>Core's art cache once the runtime is up (art requests can arrive before it is); null if startup failed.</summary>
    public async Task<Glider.Core.Art.ArtCache?> GetArtCacheAsync()
    {
        try
        {
            await RpcTask.ConfigureAwait(false);
            return Composition.Runtime?.Service.Art;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Task<CoreWebView2Environment> StartEnvironment(AppPaths paths)
    {
        try
        {
            return WebViewSetup.CreateEnvironmentAsync(paths);
        }
        catch (Exception ex)
        {
            return Task.FromException<CoreWebView2Environment>(ex);
        }
    }
}
