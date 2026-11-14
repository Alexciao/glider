using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Services;
using Glider.Core.Tests.Egl;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;
using EglProbe = Glider.Core.Tests.Egl.FakeProcesses;
using GameProcesses = Glider.Core.Tests.Services.FakeProcesses;

namespace Glider.Core.Tests.RealLegendary;

/// <summary>
/// The real <see cref="GliderRuntime"/> over a temp data dir whose legendary.exe is the REAL one (compare EndToEnd/EndToEndHarness, which
/// drives FakeLegendary): migration, stores, LegendaryCli, JobQueue, EglService and GliderService all run for real. Only the host window,
/// the process list/starter, the disk, the registry and the EGL ProgramData tree (a temp one) are faked, so nothing outside the temp dir is
/// touched and no game is started. Legendary's library files are seeded from <see cref="VerifyFixture"/>.
/// </summary>
internal sealed class RealRuntimeHarness : IAsyncDisposable
{
    private RealRuntimeHarness(RealLegendaryEnv env) => Env = env;

    public RealLegendaryEnv Env { get; }

    public EglTestTree Tree { get; } = new();

    public EglProbe EglProcesses { get; } = new();

    public FakeHost Host { get; } = new(new CallLog());

    public FakeSink Sink { get; } = new();

    public FakeDisk Disk { get; } = new();

    public GameProcesses GameProcesses { get; } = new();

    public GliderRuntime Runtime { get; private set; } = null!;

    public IGliderRpc Rpc => Runtime.Rpc;

    public string App => VerifyFixture.App;

    public string Folder { get; private set; } = "";

    public static async Task<RealRuntimeHarness> StartAsync(bool signedIn = true, bool installed = true)
    {
        var h = new RealRuntimeHarness(new RealLegendaryEnv());
        if (installed)
        {
            h.Folder = VerifyFixture.Install(h.Env);
        }
        else
        {
            h.Env.Library.Add(VerifyFixture.App, LibraryFixture.Item("Glider Verify", "fakens", "00000000000000000000000000000001"), VerifyFixture.Version).Save();
        }

        if (signedIn)
        {
            h.Env.WriteFakeUser();
        }

        await h.BootAsync();
        return h;
    }

    private async Task BootAsync()
    {
        Runtime = await GliderRuntime.StartAsync(Env.Paths, Host, Sink, Tree.Environment, options: new GliderRuntimeOptions
        {
            Legendary = new LegendaryOptions { StallTimeout = TimeSpan.FromMinutes(5), ProbeCdn = false, Runner = RealLegendaryEnv.OfflineRunner },
            EglProcesses = EglProcesses,
            Egl = EglTestTree.FastOptions(),
            ProcessMonitor = GameProcesses,
            ProcessStarter = GameProcesses,
            DiskInfo = Disk,
            FreeGamesBackground = false, // never touch Epic from tests
            ServiceQuery = new FakeServices { Installed = { "EasyAntiCheat_EOS" } },
            LibraryCoalesceInterval = TimeSpan.Zero,
            LibraryCacheDelay = TimeSpan.Zero,
            LockRetryDelay = TimeSpan.FromMilliseconds(200),
            OverlayRegistry = new FakeOverlayRegistry(),
            OverlayEnvironment = new Glider.Core.Overlay.OverlayEnvironment(null),
            ThirdPartyLaunchers = new FakeLaunchers(),
        });
        await Runtime.Service.WarmupTask;
        await Runtime.Service.PrewarmForTests.LibrarySync;
        await Runtime.Service.LibraryForTests.FlushAsync();
        // Balanced runs a verify below normal CPU/IO priority; on a busy build machine that starves it for minutes. These tests are about
        // what Legendary says, not about priorities (DownloadModeTests covers those), so run at Full speed.
        await Rpc.UpdateSettings(Snapshot.Settings with { DownloadMode = DownloadMode.Full });
    }

    public AppSnapshot Snapshot => Runtime.Service.Hub.BuildSnapshot();

    public GameSummary? Game() => Runtime.Service.LibraryForTests.Summary(App);

    public JobInfo Job(string id) => Snapshot.Queue.Jobs.Single(j => j.Id == id);

    public static async Task<T> WaitFor<T>(Func<T> read, Func<T, bool> until, string what, int timeoutMs = 90_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var v = read();
            if (until(v))
            {
                return v;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}; last value: {v}");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public Task<JobInfo> WaitForDone(string id, string what) =>
        WaitFor(() => Job(id), j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled, what);

    public async ValueTask DisposeAsync()
    {
        if (Runtime is not null)
        {
            await Runtime.DisposeAsync();
        }

        Env.Dispose();
        Tree.Dispose();
    }
}
