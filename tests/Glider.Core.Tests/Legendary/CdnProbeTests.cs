using System.Text.Json;
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

    [Fact]
    public async Task PicksTheFirstGoodHostInEpicsOrder_NotTheFastest()
    {
        var plan = await ProbeAsync(new Dictionary<string, Host>
        {
            ["egdownload.fastly-edge.com"] = new(Ttfb: 15, ReadDelayMs: 10),
            ["epicgames-download1.akamaized.net"] = new(Ttfb: 15, ReadDelayMs: 2), // faster, but later in Epic's order
        });

        Assert.NotNull(plan);
        Assert.Equal("egdownload.fastly-edge.com", plan.Pick?.Host);
        Assert.Equal("ok", plan.Find("epicgames-download1.akamaized.net")!.Verdict);
    }

    [Fact]
    public async Task DropsHostsWithHighTtfbFailuresOrALowShareOfTheBestRate()
    {
        var plan = await ProbeAsync(new Dictionary<string, Host>
        {
            ["egdownload.fastly-edge.com"] = new(Ttfb: 400, ReadDelayMs: 1), // akamai-like slow first byte
            ["epicgames-download1.akamaized.net"] = new(Ttfb: 10, Fail: true),
            ["egs-cloudfront-chunks.epicgamescdn.com"] = new(Ttfb: 10, ReadDelayMs: 900), // passes TTFB, far below half of the best
            ["fastly-download.epicgames.com"] = new(Ttfb: 10, ReadDelayMs: 5),
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
    public async Task FailoverWalksTheGoodHostsThenStops()
    {
        var plan = await ProbeAsync(EpicHosts.ToDictionary(h => h, _ => new Host(Ttfb: 10, ReadDelayMs: 5)));

        Assert.NotNull(plan);
        var seen = new List<string>();
        for (var h = plan.Pick; h is not null; h = plan.Next(h.Host))
        {
            seen.Add(h.Host);
        }

        Assert.Equal(EpicHosts, seen.Take(3));
        Assert.NotNull(plan.ProbeMegabytesPerSecond("egdownload.fastly-edge.com"));
    }

    [Fact]
    public async Task NeedsAChoiceAndEnoughChunks()
    {
        var probe = new CdnProbe(new TestLog(), () => new FakeCdn(new Dictionary<string, Host>()));
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

    private static Task<CdnPlan?> ProbeAsync(Dictionary<string, Host> hosts) =>
        new CdnProbe(new TestLog(), () => new FakeCdn(hosts)) { TotalBudget = TimeSpan.FromSeconds(30), HostTimeout = TimeSpan.FromSeconds(10) }
            .ProbeAsync(Epic(EpicHosts), Paths, TestContext.Current.CancellationToken);

    /// <summary>A CDN host: time to first byte, then a 1 MB body that arrives after <see cref="ReadDelayMs"/>; or an error.</summary>
    private sealed record Host(int Ttfb, int ReadDelayMs = 0, bool Fail = false);

    private sealed class FakeCdn(Dictionary<string, Host> hosts) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!hosts.TryGetValue(request.RequestUri!.Host, out var host) || host.Fail)
            {
                await Task.Delay(5, cancellationToken);
                throw new HttpRequestException("connection refused");
            }

            await Task.Delay(host.Ttfb, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(host.ReadDelayMs)) };
        }
    }

    private sealed class SlowStream(int delayMs) : Stream
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

            await Task.Delay(delayMs, cancellationToken);
            var n = Math.Min(buffer.Length, 1_000_000);
            buffer.Span[..n].Clear();
            return n;
        }
    }
}
