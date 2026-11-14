using System.Text.Json.Nodes;
using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Saves;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Saves;

/// <summary>
/// The real process path: LegendarySavesCli over the runner against FakeLegendary.exe, whose `cloudsaves` directive simulates
/// Epic's cloud with Legendary 0.21.1's own decision logic and log lines. Pins the exact flags Glider sends and how it reads
/// the (all-human-text) output.
/// </summary>
public class LegendarySavesCliTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Env : IDisposable
    {
        public Env()
        {
            Legendary = new FakeLegendaryEnv();
            SaveDir = Path.Combine(Legendary.Root, "saves");
            Directory.CreateDirectory(SaveDir);
            Runner = new LegendaryProcessRunner(Legendary.Paths, Legendary.Log);
            Cli = new LegendarySavesCli(Runner, Legendary.Log);
            Legendary.Script("list-saves", "cloudsaves list");
            Sync("cloudsaves sync");
            File.WriteAllText(Path.Combine(Legendary.FakeDir, "savepath.Saver"), SaveDir);
        }

        public FakeLegendaryEnv Legendary { get; }
        public string SaveDir { get; }
        public LegendaryProcessRunner Runner { get; }
        public LegendarySavesCli Cli { get; }

        public void Sync(string script) => Legendary.Script("sync-saves", script);

        public void Installed(string? savePath)
        {
            var game = new JsonObject { ["app_name"] = "Saver", ["install_path"] = "C:/Games/Saver", ["version"] = "1" };
            if (savePath is not null)
            {
                game["save_path"] = savePath;
            }

            File.WriteAllText(Path.Combine(Legendary.Config, "installed.json"), new JsonObject { ["Saver"] = game }.ToJsonString());
        }

        public string? StoredPath() =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(Legendary.Config, "installed.json")))!["Saver"]!["save_path"]?.GetValue<string>();

        public void Local(string rel, string text, DateTime mtime)
        {
            var full = Path.Combine(SaveDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            File.SetLastWriteTimeUtc(full, mtime);
        }

        public void Cloud(DateTime at, string rel, string text)
        {
            var full = Path.Combine(Legendary.FakeDir, "cloud", "Saver", at.ToString("yyyy.MM.dd-HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture), rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public string[] LastArgs(string command) => Legendary.Invocations().Last(i => i.Command == command).Args;

        public void Dispose()
        {
            Runner.Dispose();
            Legendary.Dispose();
        }
    }

    [Fact]
    public async Task ListsTheSavesOfOneGame()
    {
        using var e = new Env();
        e.Cloud(T0, "a.sav", "1");
        e.Cloud(T0.AddHours(5), "a.sav", "2");

        var cloud = await e.Cli.ListSavesAsync("Saver", TestContext.Current.CancellationToken);

        Assert.Equal(["2026.11.10-12.00.00.manifest", "2026.11.10-17.00.00.manifest"], cloud.Manifests);
        Assert.Equal(["list-saves", "Saver"], e.LastArgs("list-saves"));
    }

    [Fact]
    public async Task ASignedOutListFailsLoudly_NeverAsAnEmptyCloud()
    {
        using var e = new Env();
        e.Legendary.Script("list-saves", "err [cli] ERROR: Login failed! Cannot continue with download process.\nexit 1");

        var ex = await Assert.ThrowsAsync<GliderException>(() => e.Cli.ListSavesAsync("Saver", TestContext.Current.CancellationToken));

        Assert.NotEqual(ErrorCode.Unknown, ex.Error.Code);
    }

    [Fact]
    public async Task SetupAcceptsLegendarysPathOnceAndTouchesNothingElse()
    {
        using var e = new Env();
        e.Installed(null);
        e.Local("a.sav", "keep", T0);
        e.Cloud(T0.AddDays(9), "a.sav", "cloud");

        var run = await e.Cli.SyncAsync("Saver", SyncMode.SetupPath, TestContext.Current.CancellationToken);

        Assert.Null(run.Error);
        Assert.Equal(["-y", "sync-saves", "Saver", "--skip-upload", "--skip-download", "--accept-path"], e.LastArgs("sync-saves"));
        Assert.Equal(e.SaveDir, e.StoredPath());
        Assert.Equal("keep", File.ReadAllText(Path.Combine(e.SaveDir, "a.sav"))); // both skip flags: no transfer even though the cloud is newer
        Assert.False(run.DownloadReported || run.UploadFinished);
    }

    [Fact]
    public async Task WithoutAStoredPathLegendarySkips_AndGlidersReadsThatAsUnresolved()
    {
        using var e = new Env();
        e.Installed(null);
        e.Sync("cloudsaves sync"); // setup flag not passed by a bare call: the fake behaves like `-y` without --accept-path
        var run = await e.Cli.SyncAsync("Saver", SyncMode.Upload, TestContext.Current.CancellationToken);

        Assert.True(run.PathUnresolved);
        Assert.False(run.UploadFinished);
        Assert.Null(e.StoredPath());
    }

    [Fact]
    public async Task UploadIsOneExplicitDirection_AndTheCloudThenHasTheSave()
    {
        using var e = new Env();
        e.Installed(e.SaveDir);
        e.Local("a.sav", "progress", T0);

        var run = await e.Cli.SyncAsync("Saver", SyncMode.Upload, TestContext.Current.CancellationToken);

        Assert.Equal(["-y", "sync-saves", "Saver", "--skip-download", "--force-upload"], e.LastArgs("sync-saves"));
        Assert.True(run.UploadFinished);
        Assert.Null(run.Error);
        var cloud = await e.Cli.ListSavesAsync("Saver", TestContext.Current.CancellationToken);
        Assert.True(SaveSyncPlanner.UploadLanded([], cloud.Manifests, T0));
    }

    [Fact]
    public async Task ForceDownloadReplacesAnewerLocalSave_WhichIsWhyGliderBacksUpFirst()
    {
        using var e = new Env();
        e.Installed(e.SaveDir);
        e.Local("a.sav", "NEWER local progress", T0.AddDays(30)); // Legendary alone would call local newer and skip the download
        e.Cloud(T0, "a.sav", "cloud save");

        var run = await e.Cli.SyncAsync("Saver", SyncMode.Download, TestContext.Current.CancellationToken);

        Assert.Equal(["-y", "sync-saves", "Saver", "--skip-upload", "--force-download"], e.LastArgs("sync-saves"));
        Assert.True(run.DownloadReported);
        Assert.False(run.DownloadProblems);
        Assert.Equal("cloud save", File.ReadAllText(Path.Combine(e.SaveDir, "a.sav")));
        Assert.Equal(T0, File.GetLastWriteTimeUtc(Path.Combine(e.SaveDir, "a.sav"))); // mtime = the manifest's time
        var after = SaveFolder.Scan(e.SaveDir, SaveFilters.None, ct: TestContext.Current.CancellationToken);
        Assert.True(SaveSyncPlanner.DownloadLanded(after, await e.Cli.ListSavesAsync("Saver", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task ADroppedUploadPrintsSuccess_ButTheListingGivesItAway()
    {
        using var e = new Env();
        e.Installed(e.SaveDir);
        e.Local("a.sav", "progress", T0);
        e.Sync("cloudsaves sync fault=dropupload");

        var run = await e.Cli.SyncAsync("Saver", SyncMode.Upload, TestContext.Current.CancellationToken);

        Assert.True(run.UploadFinished); // "Finished uploading savegame." was printed
        var cloud = await e.Cli.ListSavesAsync("Saver", TestContext.Current.CancellationToken);
        Assert.False(SaveSyncPlanner.UploadLanded([], cloud.Manifests, T0));
    }

    [Fact]
    public async Task AFailedDownloadStillSaysSuccessAfterDeletingEverything_SoGliderLooksAtTheLog_AndTheDisk()
    {
        using var e = new Env();
        e.Installed(e.SaveDir);
        e.Local("a.sav", "original", T0);
        e.Cloud(T0.AddDays(1), "a.sav", "cloud");
        e.Sync("cloudsaves sync fault=failafterdelete");

        var run = await e.Cli.SyncAsync("Saver", SyncMode.Download, TestContext.Current.CancellationToken);

        Assert.True(run.DownloadReported);
        Assert.True(run.DownloadProblems);
        Assert.False(File.Exists(Path.Combine(e.SaveDir, "a.sav"))); // already deleted: only Glider's backup has it
        var after = SaveFolder.Scan(e.SaveDir, SaveFilters.None, ct: TestContext.Current.CancellationToken);
        Assert.False(SaveSyncPlanner.DownloadLanded(after, await e.Cli.ListSavesAsync("Saver", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task AKilledDownloadSurfacesAsAFailureNotASuccess()
    {
        using var e = new Env();
        e.Installed(e.SaveDir);
        e.Local("a.sav", "original", T0);
        e.Cloud(T0.AddDays(1), "a.sav", "cloud");
        e.Sync("cloudsaves sync fault=killafterdelete");

        var run = await e.Cli.SyncAsync("Saver", SyncMode.Download, TestContext.Current.CancellationToken);

        Assert.False(run.DownloadReported);
        Assert.False(run.UploadFinished);
    }
}
