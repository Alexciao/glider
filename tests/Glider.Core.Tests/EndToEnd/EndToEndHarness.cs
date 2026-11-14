using System.Diagnostics;
using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Services;
using Glider.Core.Tests.Egl;
using Glider.Core.Tests.TestSupport;
using EglProbe = Glider.Core.Tests.Egl.FakeProcesses;
using GameProcesses = Glider.Core.Tests.Services.FakeProcesses;

namespace Glider.Core.Tests.EndToEnd;

/// <summary>
/// The real <see cref="GliderRuntime"/> (migration, FileLog, stores, LegendaryCli, JobQueue, EglService, GliderService) over a
/// temp data dir, driving tests/FakeLegendary as legendary.exe, with a temp EGL ProgramData tree and Legendary's library files
/// seeded from the real catalog fixtures. Only the host window, process list and disk are faked. Restartable:
/// <see cref="RestartAsync"/> keeps the data dir.
/// </summary>
internal sealed class EndToEndHarness : IAsyncDisposable
{
    public const string Account = "Tester";
    public const string Fn = FortniteLayout.AppName;

    private EndToEndHarness() => Egl = Tree.Environment;

    /// <summary>The temp EGL ProgramData tree (its Paths are unused; Glider's data lives in <see cref="Legendary"/>).</summary>
    public EglTestTree Tree { get; } = new();

    public FakeLegendaryEnv Legendary { get; } = new();
    public EglEnvironment Egl { get; }
    public EglProbe EglProcesses { get; } = new();
    public Services.FakeHost Host { get; } = new(new Services.CallLog());
    public Services.FakeSink Sink { get; } = new();
    public Services.FakeDisk Disk { get; } = new();
    public GameProcesses GameProcesses { get; } = new();
    public Services.FakeServices EacInstalled { get; } = new() { Installed = { "EasyAntiCheat_EOS" } };
    public GliderRuntime Runtime { get; private set; } = null!;
    public IGliderRpc Rpc => Runtime.Rpc;
    public LibraryFixture Library { get; private set; } = null!;

    public string GamesRoot => Legendary.Root + Path.DirectorySeparatorChar + "Games";

    public static async Task<EndToEndHarness> StartAsync(string latestVersion, bool signedIn = true)
    {
        var h = new EndToEndHarness();
        h.Library = LibraryFixture.Fortnite(h.Legendary.Config, latestVersion).Save();
        h.Library.WritesInstalled = false; // from now on the fake exe owns installed.json
        h.DefaultScripts(latestVersion);
        if (signedIn)
        {
            h.WriteUserJson();
        }

        await h.BootAsync();
        return h;
    }

    public async Task RestartAsync()
    {
        await Runtime.DisposeAsync();
        await BootAsync();
    }

    private async Task BootAsync()
    {
        Runtime = await GliderRuntime.StartAsync(Legendary.Paths, Host, Sink, Egl, options: new GliderRuntimeOptions
        {
            Legendary = new LegendaryOptions { StallTimeout = TimeSpan.FromSeconds(30) },
            EglProcesses = EglProcesses,
            Egl = EglTestTree.FastOptions(),
            ProcessMonitor = GameProcesses,
            ProcessStarter = GameProcesses,
            DiskInfo = Disk,
            FreeGamesBackground = false, // never touch Epic from tests
            ServiceQuery = EacInstalled,
            LibraryCoalesceInterval = TimeSpan.Zero,
            LibraryCacheDelay = TimeSpan.Zero,
            LockRetryDelay = TimeSpan.FromMilliseconds(50),
        });
        await Runtime.Service.WarmupTask;
        await Runtime.Service.PrewarmForTests.LibrarySync;
        await Runtime.Service.LibraryForTests.FlushAsync();
    }

    public void WriteUserJson() =>
        File.WriteAllText(Path.Combine(Legendary.Config, "user.json"), $$"""{"displayName": "{{Account}}", "account_id": "acc-1", "access_token": "eg1~x"}""");

    /// <summary>Legendary's answers for the commands every scenario touches.</summary>
    public void DefaultScripts(string latestVersion)
    {
        Legendary.Script("status", $$"""out {"account": "{{Account}}", "games_available": 1, "games_installed": 0, "egl_sync_enabled": false, "config_directory": "x"}""");
        Info(latestVersion);
        Legendary.Script("auth", $$"""
            writefile user.json {"displayName": "{{Account}}", "account_id": "acc-1", "access_token": "eg1~x"}
            err [cli] INFO: Testing existing login data if present...
            err [cli] INFO: Successfully logged in as "{{Account}}"
            """);
        Legendary.Script("list", """
            err [Core] INFO: Trying to re-use existing login session...
            err [cli] INFO: Getting game list... (this may take a while)
            out []
            """);
        Legendary.Script("list-installed", """
            err [Core] INFO: Trying to re-use existing login session...
            err [cli] INFO: Checking for updates...
            out []
            """);
        Legendary.Script("verify", "verify files=5 interval=1");
        Legendary.Script("uninstall", """
            uninstalled
            err [cli] INFO: Game has been uninstalled.
            """);
        Import(latestVersion);
        Install(latestVersion, "interval=5");
    }

    public void Info(string version) =>
        Legendary.Script("info", $$"""out {"game": {"app_name": "Fortnite", "title": "Fortnite", "version": "{{version}}"}, "install": null, "manifest": null}""");

    public void Import(string version) => Legendary.Script("import", $"""
        err [Core] INFO: Trying to re-use existing login session...
        installed version={version} path={"{arg:1}"}
        err [cli] INFO: Game "Fortnite" has been imported.
        """);

    public void Install(string version, string downloadArgs) => Legendary.Script("install", $"""
        err [Core] INFO: Trying to re-use existing login session...
        err [cli] INFO: Preparing download for "Fortnite"...
        download chunks=400 files=8 mib=64 {downloadArgs}
        writefile {"{basepath}"}/Fortnite/{FortniteLayout.ShippingExeRelative.Replace('\\', '/')} MZ
        installed version={version} path={"{basepath}"}/Fortnite
        err [cli] INFO: Finished installation process in 0.20 seconds.
        """);

    public int Count(string command) => Legendary.Invocations().Count(i => i.Command == command);

    public AppSnapshot Snapshot => Runtime.Service.Hub.BuildSnapshot();

    /// <summary>The published summary of a game (the library rebuilds right after every change in these tests).</summary>
    public GameSummary? Game(string app = Fn) => Runtime.Service.LibraryForTests.Summary(app);

    public JobInfo Job(string id) => Snapshot.Queue.Jobs.Single(j => j.Id == id);

    public static async Task<T> WaitFor<T>(Func<T> read, Func<T, bool> until, string what, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var v = read();
            if (until(v))
            {
                return v;
            }

            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"timed out waiting for {what}; last value: {v}");
            }

            await Task.Delay(10);
        }
    }

    public Task<JobInfo> WaitForJob(string id, Func<JobInfo, bool> until, string what) => WaitFor(() => Job(id), until, what);

    public Task<JobInfo> WaitForDone(string id, string what) => WaitForJob(id, j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled, what);

    /// <summary>A Fortnite folder EGL installed (shipping exe + .egstore) and its .item at <paramref name="version"/>.</summary>
    public (string Game, string Guid) EglInstall(string version)
    {
        const string guid = "11111111111111111111111111111111";
        var game = Tree.CreateGame(Tree.Drive("E", "Epic Games", "Fortnite"), guid);
        Tree.WriteItem(game, version, guid);
        return (game, guid);
    }

    /// <summary>Imports a folder through the RPC (an Import job) and waits for it (and any verify it queued) to finish.</summary>
    public async Task<JobInfo> ImportAsync(string folder, VerifyMode mode = VerifyMode.Auto)
    {
        var job = await Rpc.ImportFolder(new LocateRequest(folder, mode));
        var done = await WaitForDone(job.Id, "import");
        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        await Runtime.Service.LibraryForTests.FlushAsync();
        return done;
    }

    public async ValueTask DisposeAsync()
    {
        if (Runtime is not null)
        {
            await Runtime.DisposeAsync();
        }

        Legendary.Dispose();
        Tree.Dispose();
    }
}
