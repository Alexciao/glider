using System.Runtime.InteropServices;
using Glider.Core;
using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.App;

/// <summary>
/// The integration point with Glider.Core: builds and starts <see cref="GliderRuntime"/> (the real object graph).
/// Called once, on a thread-pool thread, in parallel with window creation — it must not touch the UI thread.
/// </summary>
internal static class Composition
{
    public static GliderRuntime? Runtime { get; private set; }

    public static async Task<IGliderRpc> BuildAsync(AppPaths paths, IHostPlatform host, IEventSink events, Glider.Core.Links.IShellIntegration? shell = null)
    {
        Runtime = await GliderRuntime.StartAsync(paths, host, events, idleSignal: new WindowsIdleSignal(), options: new GliderRuntimeOptions { Shell = shell, ImageProcessor = new WpfImageProcessor(), WarmSerializers = true }).ConfigureAwait(false);
        return Runtime.Rpc;
    }

    /// <summary>Disposes Core on a context-free pool thread (see <see cref="ShutdownRunner"/>); logs how long it took.</summary>
    public static void Dispose()
    {
        if (Runtime is not { } runtime)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var finished = ShutdownRunner.DisposeBlocking(runtime.DisposeAsync, DisposeTimeout, ex => HostLog.Warn("Disposing the Core runtime failed", ex));
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        HostLog.Info(finished ? $"Core runtime disposed in {ms:0} ms" : $"Core runtime disposal gave up after {ms:0} ms");
    }

    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);
}

/// <summary>Time since the last keyboard/mouse input (for "auto-update only when idle").</summary>
internal sealed partial class WindowsIdleSignal : IIdleSignal
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    public TimeSpan IdleTime
    {
        get
        {
            var info = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }

            var idleMs = unchecked((uint)Environment.TickCount - info.DwTime);
            return TimeSpan.FromMilliseconds(idleMs);
        }
    }
}
