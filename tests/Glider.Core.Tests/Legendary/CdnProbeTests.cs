using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

/// <summary>The manifest chunk list (golden: written and read back by Legendary's own code).</summary>
public class ManifestReaderTests
{
    private static byte[] Golden => File.ReadAllBytes(Path.Combine(Fixture.Dir, "manifest", "glider-test.manifest"));

    [Fact]
    public void ReadsTheSameChunkPathsAndSizesAsLegendary()
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixture.Dir, "manifest", "glider-test.paths.json")));
        var table = ManifestReader.TryReadChunks(Golden);

        Assert.NotNull(table);
        var rows = expected.RootElement.EnumerateArray().ToList();
        Assert.Equal(rows.Count, table.Count);
        Assert.Equal(21, table.FeatureLevel);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(rows[i][0].GetString(), table.Path(i));
            Assert.Equal(rows[i][1].GetInt64(), table.CompressedSize(i));
            Assert.Equal(rows[i][2].GetInt32(), table.WindowSize(i));
        }
    }

    [Fact]
    public void ProbeChunksAreFullSizedDistinctAndSpreadOut()
    {
        var table = ManifestReader.TryReadChunks(Golden)!;
        var picks = table.PickProbeChunks(8);

        Assert.Equal(8, picks.Count);
        Assert.Equal(8, picks.Distinct().Count());
        var bySize = Enumerable.Range(0, table.Count).ToDictionary(table.Path, table.CompressedSize);
        Assert.All(picks, p => Assert.True(bySize[p] >= 900 * 1024, p));
        Assert.NotEqual(picks, table.PickProbeChunks(8, seed: 3)); // a different job fetches different objects
    }

    [Fact]
    public void UnreadableManifestsGiveNullInsteadOfThrowing()
    {
        var golden = Golden;
        Assert.Null(ManifestReader.TryReadChunks([]));
        Assert.Null(ManifestReader.TryReadChunks(golden.AsSpan(0, 30)));
        Assert.Null(ManifestReader.TryReadChunks(golden.AsSpan(0, golden.Length / 2))); // truncated zlib body
        var damaged = (byte[])golden.Clone();
        damaged[^3] ^= 0xFF;
        Assert.Null(ManifestReader.TryReadChunks(damaged)); // body hash mismatch
        var v22 = (byte[])golden.Clone();
        v22[37] = 22; // serialisation version 22 = ChunksV5 with per-chunk secrets
        Assert.Null(ManifestReader.TryReadChunks(v22));
        var encrypted = (byte[])golden.Clone();
        encrypted[36] |= 2;
        Assert.Null(ManifestReader.TryReadChunks(encrypted));
    }
}

/// <summary>The job-start CDN probe (docs/download-perf.md §5.1).</summary>
public class CdnProbeTests
{
    private static readonly string[] EpicHosts = ["egdownload.fastly-edge.com", "epicgames-download1.akamaized.net", "egs-cloudfront-chunks.epicgamescdn.com"];
    private static readonly string[] Paths = [.. Enumerable.Range(0, 8).Select(i => $"ChunksV4/0{i}/00000000000000{i}0_0000.chunk")];

    private static string[] Epic(params string[] hosts) => [.. hosts.Select(h => $"https://{h}/Builds/Fortnite/CloudDir")];

    [Fact]
    public void ExtraHostsShareEpicsCloudDirPath_OtherLayoutsGetNone()
    {
        var withExtras = CdnProbe.Candidates(Epic(EpicHosts));
        Assert.Equal(6, withExtras.Count);
        Assert.Equal(EpicHosts, withExtras.Take(3).Select(c => new Uri(c.BaseUrl).Host));
        Assert.All(withExtras.Take(3), c => Assert.True(c.InEpicList));
        Assert.All(withExtras.Skip(3), c => Assert.False(c.InEpicList));
        Assert.All(withExtras.Skip(3), c => Assert.EndsWith("/Builds/Fortnite/CloudDir", c.BaseUrl, StringComparison.Ordinal));

        Assert.Equal(2, CdnProbe.Candidates(["https://a.example/x", "https://b.example/x"]).Count);
        Assert.Equal(["https://a.example/x"], CdnProbe.Candidates(["https://a.example/x/", "https://A.example/y", "ftp://c.example/z"]).Select(c => c.BaseUrl));
    }

    private static CdnHostResult R(string host, string verdict, double mbps = 30, bool epic = true) =>
        new($"https://{host}/Builds/Fortnite/CloudDir", host, epic, verdict != "skipped", verdict == "failed", 10, mbps) { Verdict = verdict };

    [Fact]
    public void PicksTheFirstGoodHostInEpicsOrder_NotTheFastest()
    {
        var plan = new CdnPlan([R("a.example", "ok", 20), R("b.example", "ok", 60), R("c.example", "slow", 5)]);

        Assert.Equal("a.example", plan.Pick?.Host);
        Assert.Equal("b.example", plan.Next("a.example")?.Host);
        Assert.Null(plan.Next("b.example")); // slow hosts are never failover targets
        Assert.Equal(20, plan.ProbeMegabytesPerSecond("a.example"));
    }

    [Fact]
    public void SkippedHostsComeAfterGoodOnesAndBeforeNothing()
    {
        var plan = new CdnPlan([R("a.example", "failed"), R("b.example", "skipped"), R("c.example", "ok")]);

        Assert.Equal("c.example", plan.Pick?.Host);
        Assert.Equal("b.example", plan.Next("c.example")?.Host); // unprobed hosts are usable as a last resort
        Assert.Null(plan.Pick is null ? null : plan.Next("b.example"));
        Assert.Null(new CdnPlan([R("a.example", "slow")]).Pick);
    }

    [Fact]
    public async Task DropsHostsWithHighTtfbFailuresOrALowShareOfTheBestRate()
    {
        var plan = await ProbeAsync(new Dictionary<string, Host>
        {
            ["egdownload.fastly-edge.com"] = new(Ttfb: 1000, ReadDelayMs: 1), // akamai-like slow first byte
            ["epicgames-download1.akamaized.net"] = new(Ttfb: 5, Fail: true),
            ["egs-cloudfront-chunks.epicgamescdn.com"] = new(Ttfb: 5, ReadDelayMs: 3000), // passes TTFB, far below half of the best
            ["fastly-download.epicgames.com"] = new(Ttfb: 5, ReadDelayMs: 5),
        });

        Assert.NotNull(plan);
        Assert.Equal("slow", plan.Find("egdownload.fastly-edge.com")!.Verdict);
        Assert.Equal("failed", plan.Find("epicgames-download1.akamaized.net")!.Verdict);
        Assert.Equal("slow", plan.Find("egs-cloudfront-chunks.epicgamescdn.com")!.Verdict);
        Assert.Equal("fastly-download.epicgames.com", plan.Pick?.Host);
        Assert.Equal("skipped", plan.Find("download.epicgames.com")!.Verdict); // 3+ measured and one passes: stop early
        Assert.NotEqual("slow", plan.Next("fastly-download.epicgames.com")?.Verdict); // failover never goes to a bad host
        Assert.NotEqual("failed", plan.Next("fastly-download.epicgames.com")?.Verdict);
    }

    [Fact]
    public async Task NoGoodHostMeansNoPick()
    {
        var plan = await ProbeAsync(EpicHosts.ToDictionary(h => h, _ => new Host(Ttfb: 10, Fail: true)));

        Assert.NotNull(plan);
        Assert.Null(plan.Pick);
        Assert.Null(plan.Next(null));
    }

    [Fact]
    public async Task NeedsAChoiceAndEnoughChunks()
    {
        var probe = new CdnProbe(new TestLog(), () => new FakeCdn(new Dictionary<string, Host>(), new ManualTimeProvider()));
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await probe.ProbeAsync(["https://only.example/x"], Paths, ct));
        Assert.Null(await probe.ProbeAsync(Epic(EpicHosts), [Paths[0]], ct));
    }

    [Fact]
    public void ReadsBaseUrlsFromMetadataThenInstalledJson()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("metadata"));
        File.WriteAllText(dir.Combine("installed.json"), """{"Game":{"base_urls":["https://i.example/Builds/G/CloudDir"]},"Other":{"base_urls":[]}}""");
        Assert.Equal(["https://i.example/Builds/G/CloudDir"], CdnProbe.ReadBaseUrls(dir.Path, "Game"));
        Assert.Empty(CdnProbe.ReadBaseUrls(dir.Path, "Other"));

        File.WriteAllText(dir.Combine("metadata", "Game.json"), """{"app_name":"Game","base_urls":["https://m.example/Builds/G/CloudDir","https://n.example/Builds/G/CloudDir"]}""");
        Assert.Equal(2, CdnProbe.ReadBaseUrls(dir.Path, "Game").Count);

        File.WriteAllText(dir.Combine("metadata", "Game.json"), "{ torn");
        Assert.Equal(["https://i.example/Builds/G/CloudDir"], CdnProbe.ReadBaseUrls(dir.Path, "Game")); // a torn file falls back
    }

    [Fact]
    public void FindsTheNewestCachedManifestOfTheApp()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("manifests"));
        var older = dir.Combine("manifests", "Game_Windows_1.0.manifest");
        var newer = dir.Combine("manifests", "Game_Windows_2.0.manifest");
        File.WriteAllText(older, "x");
        File.WriteAllText(newer, "x");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(dir.Combine("manifests", "GameTwo_Windows_9.manifest"), "x");

        Assert.Equal(newer, CdnProbe.FindManifest(dir.Path, "Game"));
        Assert.Null(CdnProbe.FindManifest(dir.Path, "Nothing"));
    }

    /// <summary>
    /// Cached manifest + CDN list on disk, fake CDN hosts and a fake Legendary. With a <paramref name="clock"/> the CLI (stall
    /// watchdog, probe) and the fake CDN run on virtual time that only moves when the test or a fake response moves it; without
    /// one the stall timeout is a day, so a loaded machine can never read slow process start-up as a stall.
    /// </summary>
    private static (FakeLegendaryEnv Env, LegendaryCli Cli) CliWithManifest(Dictionary<string, Host> hosts, ManualTimeProvider? clock = null)
    {
        var env = new FakeLegendaryEnv();
        File.Copy(Path.Combine(Fixture.Dir, "manifest", "glider-test.manifest"), Path.Combine(env.Config, "manifests", "Game_Windows_1.0.manifest"));
        File.WriteAllText(Path.Combine(env.Config, "metadata", "Game.json"), "{\"app_name\":\"Game\",\"base_urls\":[" + string.Join(',', EpicHosts.Select(h => $"\"https://{h}/Builds/Fortnite/CloudDir\"")) + "]}");
        var cli = new LegendaryCli(env.Paths, env.Log, new LegendaryOptions
        {
            StallTimeout = clock is null ? TimeSpan.FromDays(1) : TimeSpan.FromHours(2),
            Fork = false, // the stock exe fails over by restarting; the fork switches in place
            CdnProbeHandler = () => new FakeCdn(hosts, clock ?? new ManualTimeProvider()),
        }, clock);
        return (env, cli);
    }

    private static readonly Dictionary<string, Host> GoodHosts = EpicHosts.ToDictionary(h => h, _ => new Host(Ttfb: 5, ReadDelayMs: 5));

    [Fact]
    public async Task AStalledDownloadRestartsOnceOnTheNextCdn()
    {
        var clock = new ManualTimeProvider();
        var (env, cli) = CliWithManifest(GoodHosts, clock);
        using var _ = env;
        using var __ = cli;
        // The first run writes 5 of 10 files, then the "network drops": silence until the watchdog kills it.
        env.Script("install", "download chunks=100 files=10 mib=20 interval=1 hangat=5", invocation: 1);
        env.Script("install", $$"""
            writefile {basepath}/Game/marker.txt x
            download chunks=100 files=10 mib=20 interval=1
            installed app=Game version=1.0 path={basepath}/Game
            err [cli] INFO: Finished installation process in 0.20 seconds.
            """, invocation: 2);

        var atStall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new Progress<LegendaryProgress>(p =>
        {
            if (p.Percent >= 50)
            {
                atStall.TrySetResult();
            }
        });
        var install = cli.InstallAsync(new InstallArgs("Game", InstallMode.Install, env.Root, 4, 1024, null), progress, TestContext.Current.CancellationToken);

        // Only now (5 files recorded, the last progress block seen, nothing more will ever arrive) does time move, once, past the
        // stall timeout. The first run is killed and restarted; the single jump can't land on the second run.
        await atStall.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        VirtualTime.Pass(clock, TimeSpan.FromHours(3));
        var outcome = await install.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        var installs = env.Invocations().Where(i => i.Command == "install").Select(i => i.Args).ToList();
        Assert.Equal(2, installs.Count);
        Assert.Equal("egdownload.fastly-edge.com", installs[0][Array.IndexOf(installs[0], "--preferred-cdn") + 1]);
        Assert.Equal("epicgames-download1.akamaized.net", installs[1][Array.IndexOf(installs[1], "--preferred-cdn") + 1]);
        Assert.True(env.Log.Contains("cdn probe: pick=egdownload.fastly-edge.com"));
        Assert.True(env.Log.Contains("No progress for"));
    }

    [Fact]
    public async Task AUserChosenCdnSkipsTheProbe()
    {
        var (env, cli) = CliWithManifest(GoodHosts);
        using var _ = env;
        using var __ = cli;
        env.Script("install", $$"""
            writefile {basepath}/Game/marker.txt x
            download chunks=10 files=1 mib=2 interval=1
            installed app=Game version=1.0 path={basepath}/Game
            err [cli] INFO: Finished installation process in 0.20 seconds.
            """);

        var outcome = await cli.InstallAsync(new InstallArgs("Game", InstallMode.Install, env.Root, 4, 1024, "cloudflare.epicgamescdn.com"), new Progress<LegendaryProgress>(), TestContext.Current.CancellationToken);

        Assert.True(outcome.Success, outcome.Error?.Detail + " | " + string.Join(';', env.Invocations().Select(i => i.Command)));
        var args = env.Invocations().Single(i => i.Command == "install").Args;
        Assert.Equal("cloudflare.epicgamescdn.com", args[Array.IndexOf(args, "--preferred-cdn") + 1]);
        Assert.False(env.Log.Contains("cdn probe:"));
    }

    [Fact]
    public void ExtraHostsAndTheForkControlFileReachTheCommandLine()
    {
        var args = new InstallArgs("Game", InstallMode.Update, null, 16, 1024, null, BaseUrl: "https://download.epicgames.com/Builds/Fortnite/CloudDir/");
        var a = LegendaryCli.InstallArguments(args, installed: true, 1024, false, controlFile: @"C:\x\tmp\Game.control").ToList();

        Assert.Equal("https://download.epicgames.com/Builds/Fortnite/CloudDir", a[a.IndexOf("--base-url") + 1]);
        Assert.Equal(@"C:\x\tmp\Game.control", a[a.IndexOf("--control-file") + 1]);
        Assert.DoesNotContain("--control-file", LegendaryCli.InstallArguments(args, true, 1024, false));
        Assert.DoesNotContain("--base-url", LegendaryCli.InstallArguments(args with { BaseUrl = "file:///etc" }, true, 1024, false));
    }

    /// <summary>The probe on virtual time: the fake CDN's latencies advance the clock, so real scheduling jitter can't change a verdict.</summary>
    private static Task<CdnPlan?> ProbeAsync(Dictionary<string, Host> hosts)
    {
        var clock = new ManualTimeProvider();
        return new CdnProbe(new TestLog(), () => new FakeCdn(hosts, clock), clock) { TotalBudget = TimeSpan.FromHours(1), HostTimeout = TimeSpan.FromMinutes(30), BadTtfbMs = 800, WarmTimeout = TimeSpan.FromMinutes(20) }
            .ProbeAsync(Epic(EpicHosts), Paths, TestContext.Current.CancellationToken);
    }

    /// <summary>A CDN host: time to first byte, then a 1 MB body that arrives after <see cref="ReadDelayMs"/>; or an error.</summary>
    private sealed record Host(int Ttfb, int ReadDelayMs = 0, bool Fail = false);

    /// <summary>
    /// Latency is virtual: a response moves the shared clock by its time to first byte and its body by its read delay (no real
    /// waiting). Parallel requests add up in a fixed way, so what the probe measures depends on the hosts, never on the machine.
    /// </summary>
    private sealed class FakeCdn(Dictionary<string, Host> hosts, ManualTimeProvider clock) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (!hosts.TryGetValue(request.RequestUri!.Host, out var host) || host.Fail)
            {
                VirtualTime.Pass(clock, TimeSpan.FromMilliseconds(5));
                throw new HttpRequestException("connection refused");
            }

            VirtualTime.Pass(clock, TimeSpan.FromMilliseconds(host.Ttfb));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(host.ReadDelayMs, clock)) };
        }
    }

    private sealed class SlowStream(int delayMs, ManualTimeProvider clock) : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ >= 1)
            {
                return 0;
            }

            await Task.Yield();
            VirtualTime.Pass(clock, TimeSpan.FromMilliseconds(delayMs));
            var n = Math.Min(buffer.Length, 1_000_000);
            buffer.Span[..n].Clear();
            return n;
        }
    }
}

/// <summary>Virtual time shared by the fake CDNs and the tests that drive them (<see cref="ManualTimeProvider.Advance"/> is not thread-safe on its own).</summary>
file static class VirtualTime
{
    public static void Pass(ManualTimeProvider clock, TimeSpan by)
    {
        lock (clock)
        {
            clock.Advance(by);
        }
    }
}

/// <summary>A host too slow to finish the warm-up is cut short, so one bad CDN can't eat the probe's whole time budget.</summary>
public class CdnProbeWarmTimeoutTests
{
    /// <summary>The slow host never answers: its requests wait until cancelled. Every other host answers after 5 virtual ms.</summary>
    private sealed class Handler(ManualTimeProvider clock, string slowHost, TaskCompletionSource warmUpsInFlight) : HttpMessageHandler
    {
        private int _slowRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == slowHost)
            {
                if (Interlocked.Increment(ref _slowRequests) == 4)
                {
                    warmUpsInFlight.TrySetResult(); // the whole warm-up (4 connections) is now stuck on this host
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            await Task.Yield();
            VirtualTime.Pass(clock, TimeSpan.FromMilliseconds(5));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100_000]) };
        }
    }

    [Fact]
    public async Task ASlowHostIsCutAtTheWarmTimeoutAndLeavesTimeForTheNext()
    {
        string[] paths = [.. Enumerable.Range(0, 8).Select(i => $"ChunksV4/00/{i:X16}_0000.chunk")];
        var clock = new ManualTimeProvider();
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warm = TimeSpan.FromMilliseconds(300);
        var probe = new CdnProbe(new TestLog(), () => new Handler(clock, "slow.example", stuck), clock) { WarmTimeout = warm };
        var ct = TestContext.Current.CancellationToken;
        var start = clock.GetTimestamp();

        var run = probe.ProbeAsync(["https://slow.example/Builds/G/CloudDir", "https://fast.example/Builds/G/CloudDir"], paths, ct);
        await stuck.Task.WaitAsync(TimeSpan.FromSeconds(60), ct); // the whole warm-up is waiting on the slow host
        VirtualTime.Pass(clock, warm + TimeSpan.FromMilliseconds(1)); // exactly past the warm-up limit, well inside the 3 s host limit
        var plan = await run.WaitAsync(TimeSpan.FromSeconds(60), ct);

        Assert.Equal("slow", plan!.Find("slow.example")!.Verdict); // cut by the warm timeout (the host limit would say "failed")
        Assert.Equal("fast.example", plan.Pick?.Host); // and the budget was still there for the next host
        Assert.True(clock.GetElapsedTime(start) < TimeSpan.FromSeconds(1), $"virtual time spent: {clock.GetElapsedTime(start)}");
    }
}
