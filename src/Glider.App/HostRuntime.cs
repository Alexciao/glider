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

        // 1) Browser-process startup is the long pole, so it starts first and overlaps everything below.
        EnvTask = StartEnvironment(paths);

        // 2) Core's service layer is built on the thread pool, in parallel with window construction.
        RpcTask = Task.Run(() => Composition.BuildAsync(paths, Platform, Sink, shellIntegration ? new WindowsShellIntegration() : null));
        Bridge.Dispatcher = RpcTask.ContinueWith(
            t =>
            {
                var dispatcher = new RpcDispatcher(t.Result, Bridge, (method, ex) => HostLog.Error($"RPC {method} failed", ex));
                Sink.Attach(dispatcher);
                StartupTimeline.Mark("rpc-ready");
                return dispatcher;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
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
