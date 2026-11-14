using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Glider.Core.Contracts;
using Glider.Core.Jobs;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

/// <summary>The speed limiter: feedback law, the control-file actuator and the real thing on worker processes.</summary>
public class SpeedLimitTests
{
    [Fact]
    public void FeedbackConvergesOnTheLimitWithoutOscillating()
    {
        const double capacity = 40e6;
        const long limit = 10_000_000;
        var rnd = new Random(7);
        var f = 1.0;
        var history = new List<double>();
        for (var step = 0; step < 25; step++)
        {
            var measured = capacity * f * (0.9 + 0.2 * rnd.NextDouble()); // ±10 % noise per 1 s block
            f = SpeedGovernor.NextFraction(f, limit, measured);
            history.Add(capacity * f);
        }

        Assert.All(history.Skip(10), r => Assert.InRange(r, 0.7 * limit, 1.3 * limit));
        Assert.Equal(1, SpeedGovernor.NextFraction(0.2, null, 5e6)); // no limit: fully open
        Assert.InRange(SpeedGovernor.NextFraction(0.5, 10_000_000, 0), 0.7, 0.8); // nothing measured: open up gently
        Assert.True(SpeedGovernor.NextFraction(0.0001, 1, 1e12) >= SpeedGovernor.MinFraction); // never fully stopped
    }

    [Fact]
    public void ForkAimdShrinksAboveTheLimitAndGrowsBelowIt()
    {
        Assert.Equal(12, SpeedGovernor.NextMaxActive(16, 32, 10_000_000, 15e6));
        Assert.Equal(17, SpeedGovernor.NextMaxActive(16, 32, 10_000_000, 5e6));
        Assert.Equal(16, SpeedGovernor.NextMaxActive(16, 32, 10_000_000, 9.5e6)); // within 90–100 %: hold
        Assert.Equal(32, SpeedGovernor.NextMaxActive(16, 32, null, 5e6));
        Assert.Equal(1, SpeedGovernor.NextMaxActive(1, 32, 10_000_000, 99e6));
        Assert.Equal(32, SpeedGovernor.NextMaxActive(32, 32, 10_000_000, 1e6));
    }

    [Fact]
    public void ControlFileIsWrittenAtomicallyAndRemovedAtTheEnd()
    {
        using var dir = new TempDir();
        var path = dir.Combine("tmp", "Game.control");
        using (var a = new ControlFileActuator(path, workers: 16))
        {
            a.Retarget(10_000_000, 20e6, []);
            Assert.Equal("""{"max_active": 24}""", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
            a.Pin(4);
            Assert.Equal("""{"max_active": 4}""", File.ReadAllText(path));
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ThrottlesRealWorkerProcessesToTheLimitAndReleasesThemWhenItIsRemoved()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Job Objects and process suspension are Windows-only");
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        long served = 0;
        var chunk = new byte[1 << 20];
        var accept = Task.Run(async () =>
        {
            while (true)
            {
                var c = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(async () =>
                {
                    using var client = c;
                    var stream = client.GetStream();
                    var buf = new byte[1024];
                    var piece = 64 * 1024;
                    while (await stream.ReadAsync(buf, ct) > 0)
                    {
                        // A CDN-like 10 MB/s per connection (about 40 MB/s for four workers), paced piece by piece like a real transfer.
                        var sw = Stopwatch.StartNew();
                        for (var sent = 0; sent < chunk.Length; sent += piece)
                        {
                            await stream.WriteAsync(chunk.AsMemory(sent, piece), ct);
                            Interlocked.Add(ref served, piece);
                            var ahead = TimeSpan.FromSeconds((sent + piece) / 10e6) - sw.Elapsed;
                            if (ahead > TimeSpan.Zero)
                            {
                                await Task.Delay(ahead, ct);
                            }
                        }
                    }
                }, ct);
            }
        }, ct);

        using var env = new FakeLegendaryEnv();
        env.Script("install", $$"""
            err [DLManager] INFO: Download Manager running with process-id: {pid}
            worker {{port}}
            worker {{port}}
            worker {{port}}
            worker {{port}}
            err [DLManager] INFO: Starting file writing worker...
            child writer
            hang
            """);
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var control = new RunControl(DownloadMode.Full, speedLimitBytesPerSecond: 10_000_000);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.Install, ["install", "Fortnite"])
        {
            Timeout = Timeout.InfiniteTimeSpan,
            Resources = new ResourceProfile(LowPriority: false) { Control = control },
            DownloadWorkers = 4,
        }, null, cts.Token);

        // Stand-in for Legendary's once-a-second progress block: report the rate the "CDN" actually served.
        async Task<double> MeasureAsync(int seconds)
        {
            var start = Interlocked.Read(ref served);
            var sw = Stopwatch.StartNew();
            long last = start;
            for (var i = 0; i < seconds; i++)
            {
                await Task.Delay(1000, ct);
                var now = Interlocked.Read(ref served);
                control.ReportRawRate(now - last);
                last = now;
            }

            return (Interlocked.Read(ref served) - start) / sw.Elapsed.TotalSeconds;
        }

        try
        {
            await MeasureAsync(3); // workers start, the writer is identified, the loop engages
            await MeasureAsync(8); // settle
            var limited = await MeasureAsync(6);
            Assert.True(limited is > 5e6 and < 16e6, $"limited {limited / 1e6:0.0} MB/s | {env.Log.Tail(30)}"); // 10 MB/s target; loopback unthrottled would be hundreds of MB/s

            control.Update(DownloadMode.Full, null);
            await MeasureAsync(3);
            var free = await MeasureAsync(3);
            Assert.True(free > 2 * limited, $"released rate {free / 1e6:0} MB/s should be far above the limited {limited / 1e6:0} MB/s");
        }
        finally
        {
            await cts.CancelAsync();
            listener.Stop();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            _ = accept.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
