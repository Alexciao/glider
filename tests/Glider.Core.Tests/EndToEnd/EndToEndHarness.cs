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
/// The real <see cref="GliderRuntime"/> (FileLog, stores, LegendaryCli, JobQueue, EglService, GliderService) over a temp
/// data dir, driving tests/FakeLegendary as legendary.exe, with a temp EGL ProgramData tree. Only the host window,
/// process list, disk and art download are faked. Restartable: <see cref="RestartAsync"/> keeps the data dir.
/// </summary>
internal sealed class EndToEndHarness : IAsyncDisposable
{
    public const string Account = "Tester";

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

    public string GamesRoot => Legendary.Root + Path.DirectorySeparatorChar + "Games";

    public static async Task<EndToEndHarness> StartAsync(string latestVersion, bool signedIn = true)
    {
        var h = new EndToEndHarness();
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
            ServiceQuery = EacInstalled,
            ArtCache = new Services.FakeArt(),
        });
        await Runtime.Service.WarmupTask;
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
        installed version={version} path={"{arg:1}"}
        err [cli] INFO: Game "Fortnite" has been imported.
        """);

    public void Install(string version, string downloadArgs) => Legendary.Script("install", $"""
        download chunks=400 files=8 mib=64 {downloadArgs}
        writefile {"{basepath}"}/Fortnite/{FortniteLayout.ShippingExeRelative.Replace('\\', '/')} MZ
        installed version={version} path={"{basepath}"}/Fortnite
        err [cli] INFO: Finished installation process in 0.20 seconds.
        """);

    public int Count(string command) => Legendary.Invocations().Count(i => i.Command == command);

    public AppSnapshot Snapshot => Runtime.Service.Hub.BuildSnapshot();

    public JobInfo Job(string id) => Snapshot.Jobs.Single(j => j.Id == id);

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

    /// <summary>A Fortnite folder EGL installed (shipping exe + .egstore) and its .item at <paramref name="version"/>.</summary>
    public (string Game, string Guid) EglInstall(string version)
    {
        const string guid = "11111111111111111111111111111111";
        var game = Tree.CreateGame(Tree.Drive("E", "Epic Games", "Fortnite"), guid);
        Tree.WriteItem(game, version, guid);
        return (game, guid);
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
