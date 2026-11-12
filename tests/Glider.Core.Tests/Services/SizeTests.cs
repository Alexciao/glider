using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

public class DownloadSizeTests
{
    [Fact]
    public async Task FreshInstall_UsesTheManifestDownloadSize_NotTheInstallSize()
    {
        using var rig = new Rig();
        rig.Legendary.Info = FakeLegendary.MakeInfo("v2"); // download 50 GB, install 100 GB
        await rig.StartWarmAsync();

        var game = (await rig.Service.GetSnapshot()).Game;
        Assert.Equal(50_000_000_000, game.DownloadSizeBytes);
        Assert.Equal(100_000_000_000, game.InstallSizeBytes);
    }

    [Fact]
    public async Task PendingUpdate_ReportsLegendarysDryRunDelta_NotTheWholeManifest()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = false });
        rig.Install("v1");
        rig.Legendary.Info = FakeLegendary.MakeInfo("v2");
        rig.Legendary.UpdateSizes = new LegendarySizes(1_200_000_000, 100_000_000_000);
        await rig.StartWarmAsync();
        await rig.Service.UpdateSizeTaskForTests;

        var game = (await rig.Service.GetSnapshot()).Game;
        Assert.True(game.UpdateAvailable);
        Assert.Equal(1_200_000_000, game.DownloadSizeBytes);

        await rig.Service.CheckForUpdates(); // the next check keeps the measured delta and doesn't re-run the dry run
        Assert.Equal(1_200_000_000, (await rig.Service.GetSnapshot()).Game.DownloadSizeBytes);
        Assert.Equal(1, rig.Calls.All.Count(c => c == "legendary.updateSizes"));
    }

    [Fact]
    public async Task PendingUpdate_WithoutADryRunResult_IsUnknownRatherThanTheFullGameSize()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = false });
        rig.Install("v1");
        rig.Legendary.Info = FakeLegendary.MakeInfo("v2");
        rig.Legendary.UpdateSizes = null;
        await rig.StartWarmAsync();
        await rig.Service.UpdateSizeTaskForTests;

        Assert.Null((await rig.Service.GetSnapshot()).Game.DownloadSizeBytes);
    }
}

public class UpdateDryRunTests
{
    [Fact]
    public async Task LegendaryCli_ReadsTheAnalysisSummary_WithoutConfirmingTheDownload()
    {
        await using var h = new Glider.Core.Tests.Jobs.QueueHarness();
        h.Env.UseFixture("synthetic-update-patch.stderr.txt");
        h.Env.Script("update", "errfile synthetic-update-patch.stderr.txt");

        var sizes = await h.Cli.GetUpdateSizesAsync("Fortnite", TestContext.Current.CancellationToken);

        Assert.Equal(new LegendarySizes((long)(2210.48 * 1024 * 1024), (long)(49870.20 * 1024 * 1024)), sizes);
        var (command, args) = Assert.Single(h.Env.Invocations());
        Assert.Equal("update", command);
        Assert.DoesNotContain("-y", args); // without -y Legendary stops at "Do you wish to install?"; stdin is closed, so it never proceeds
        Assert.Contains("--update-only", args);
    }
}

public class InstalledSizeTests
{
    private static async Task<GameState> GameAsync(Rig rig) => (await rig.Service.GetSnapshot()).Game;

    [Fact]
    public async Task IsMeasuredInTheBackground_AndPersistedWithTheFolderAndVersion()
    {
        using var rig = new Rig();
        using var release = new ManualResetEventSlim();
        rig.Measure = (_, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None); // a slow disk walk
            return 123_456;
        };
        rig.Install("v2");
        await rig.StartWarmAsync(); // start-up, the snapshot and RPCs all complete while the walk is still running
        Assert.Null((await GameAsync(rig)).InstalledSizeBytes);

        release.Set();
        await rig.Service.SizesForTests.IdleAsync();

        Assert.Equal(123_456, (await GameAsync(rig)).InstalledSizeBytes);
        var cache = await File.ReadAllTextAsync(Path.Combine(rig.Paths.Cache, "installed-size.json"), TestContext.Current.CancellationToken);
        Assert.Contains("123456", cache);
        Assert.Contains("\"v2\"", cache);
        Assert.Contains("Fortnite", cache);
    }

    [Fact]
    public async Task NotMeasuredYet_IsNull_AndTheCacheIsUsedWithoutWalkingTheDisk()
    {
        using var rig = new Rig();
        var rec = rig.Install("v2");
        Directory.CreateDirectory(rig.Paths.Cache);
        await File.WriteAllTextAsync(
            Path.Combine(rig.Paths.Cache, "installed-size.json"),
            $$"""{"path":{{JsonSerializer.Serialize(rec.InstallPath)}},"version":"v2","bytes":777,"measuredAt":"{{rig.Time.GetUtcNow():o}}"}""",
            TestContext.Current.CancellationToken);
        var walks = 0;
        rig.Measure = (_, _) => Interlocked.Increment(ref walks);

        await rig.Service.StartAsync();
        Assert.Null((await GameAsync(rig)).InstalledSizeBytes); // before pre-warm: nothing measured, nothing blocks
        await rig.Service.WarmupTask;
        await rig.Service.SizesForTests.IdleAsync();

        Assert.Equal(777, (await GameAsync(rig)).InstalledSizeBytes);
        Assert.Equal(0, walks);
    }

    [Fact]
    public async Task IsRecalculatedAfterAnUpdateCompletes()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = false });
        long size = 100;
        rig.Measure = (_, _) => size;
        rig.Install("v1");
        await rig.StartWarmAsync();
        await rig.Service.SizesForTests.IdleAsync();
        Assert.Equal(100, (await GameAsync(rig)).InstalledSizeBytes);

        size = 250; // the update grew the game; Legendary now reports v2
        rig.Legendary.Installed = FakeLegendary.MakeInstalled(rig.Records.Current!.InstallPath, "v2");
        var job = rig.Jobs.Enqueue(new JobRequest(JobKind.Update, null, null, "v1", "v2"));
        await rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);
        await rig.Service.SizesForTests.IdleAsync();

        Assert.Equal(250, (await GameAsync(rig)).InstalledSizeBytes);
    }

    [Fact]
    public async Task MeasurementsAreThrottled()
    {
        using var rig = new Rig { SizeMinInterval = TimeSpan.FromSeconds(30) };
        var walks = 0;
        rig.Measure = (_, _) => Interlocked.Increment(ref walks);
        rig.Install("v2");
        await rig.StartWarmAsync();
        await rig.Service.SizesForTests.IdleAsync();
        Assert.Equal(1, walks);

        rig.Service.SizesForTests.Request(filesChanged: true);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, walks); // too soon: waiting out the interval

        rig.Time.Advance(TimeSpan.FromSeconds(31));
        await rig.Service.SizesForTests.IdleAsync();
        Assert.Equal(2, walks);
    }

    [Fact]
    public async Task ShutdownCancelsARunningMeasurement()
    {
        var rig = new Rig();
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        rig.Measure = (_, ct) =>
        {
            started.TrySetResult();
            using var gate = new ManualResetEventSlim();
            using (ct.Register(gate.Set))
            {
                gate.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
            }

            if (ct.IsCancellationRequested)
            {
                cancelled.TrySetResult();
            }

            ct.ThrowIfCancellationRequested();
            return 1;
        };
        rig.Install("v2");
        await rig.Service.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        rig.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheRealWalk_SumsFileLengths_AndHandlesAMissingFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "glider-size", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        File.WriteAllBytes(Path.Combine(root, "x.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(root, "a", "y.bin"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(root, "a", "b", "z.bin"), new byte[300]);
        try
        {
            Assert.Equal(3300, InstalledSizeMeter.SumFileLengths(root, CancellationToken.None));
            Assert.Equal(0, InstalledSizeMeter.SumFileLengths(Path.Combine(root, "missing"), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
