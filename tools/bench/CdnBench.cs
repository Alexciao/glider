// Network ceiling for Epic's chunk CDN: N keep-alive HTTP/1.1 connections fetching real Fortnite chunks, bytes discarded.
// Nothing touches the disk. No auth: chunk URLs are public (<base>/ChunksV4/<group>/<hash>_<guid>.chunk).
//
// Usage:
//   dotnet run tools/bench/CdnBench.cs -- --chunks chunks.json --base https://host/Builds/Fortnite/CloudDir
//          [--conns 1,2,4,8,16,32,64] [--seconds 8] [--warmup 2] [--start <index>] [--budget-mb 4000]
//          [--proxy http://127.0.0.1:8099] [--probe] [--json out.jsonl]
//   --chunks : JSON array of [path, compressedSize, windowSize] (tools/bench/dump_chunks.py writes it).
//   --base   : repeatable; every base is measured in turn.
//   --probe  : instead of a sweep, the job-start CDN probe Glider would do (see docs/download-perf.md §5.1):
//              per base, 4 parallel GETs of 1 MiB chunks after a TLS warm-up, prints TTFB and MB/s and the winner.
// Chunks are taken sequentially from a different offset per run, so no two runs fetch the same chunk.
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

var opts = Args.Parse(args);
var chunks = JsonSerializer.Deserialize<List<JsonElement[]>>(File.ReadAllText(opts.Chunks))!
    .Select(e => (Path: e[0].GetString()!, Size: e[1].GetInt64()))
    .Where(c => c.Size > 900_000) // full 1 MiB chunks, like the bulk of a real download
    .ToArray();
Console.Error.WriteLine($"{chunks.Length} chunks loaded");
var cursor = opts.Start >= 0 ? opts.Start : Random.Shared.Next(chunks.Length);
long budget = opts.BudgetMb * 1_000_000L, spent = 0;
using var json = opts.Json is null ? null : new StreamWriter(opts.Json, append: true);

if (opts.Probe)
{
    var results = new List<(string Base, double Ttfb, double Mbps, int Errors)>();
    foreach (var b in opts.Bases)
    {
        var r = await Probe(b);
        results.Add((b, r.Ttfb, r.Mbps, r.Errors));
        Console.WriteLine($"probe {Host(b),-42} ttfb {r.Ttfb * 1000,6:F0} ms  {r.Mbps,7:F1} MB/s  errors {r.Errors}");
        json?.WriteLine(JsonSerializer.Serialize(new { kind = "probe", @base = b, ttfb_ms = r.Ttfb * 1000, mbps = r.Mbps, errors = r.Errors }));
    }

    var best = results.Where(r => r.Errors == 0).OrderByDescending(r => r.Mbps).FirstOrDefault();
    Console.WriteLine($"winner: {(best.Base is null ? "(none)" : Host(best.Base))}");
    return 0;
}

Console.WriteLine($"{"host",-42} {"conns",5} {"MB/s",8} {"Mbit/s",8} {"p50 ms",7} {"p95 ms",7} {"ttfb50",7} {"reqs",6} {"err",4} {"newconn",7}");
foreach (var b in opts.Bases)
{
    foreach (var n in opts.Conns)
    {
        if (spent > budget)
        {
            Console.Error.WriteLine("byte budget reached, stopping");
            return 0;
        }

        var r = await Sweep(b, n, opts.Seconds, opts.Warmup);
        spent += r.TotalBytes;
        Console.WriteLine($"{Host(b),-42} {n,5} {r.Mbps,8:F1} {r.Mbps * 8,8:F0} {r.P50,7:F0} {r.P95,7:F0} {r.Ttfb50,7:F0} {r.Requests,6} {r.Errors,4} {r.NewConnections,7}");
        json?.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "sweep", @base = b, conns = n, mbps = r.Mbps, p50_ms = r.P50, p95_ms = r.P95, ttfb50_ms = r.Ttfb50,
            requests = r.Requests, errors = r.Errors, new_connections = r.NewConnections, bytes = r.TotalBytes, per_second = r.PerSecond,
        }));
        json?.Flush();
    }
}

Console.Error.WriteLine($"total fetched: {spent / 1e9:F2} GB");
return 0;

(HttpClient Client, Func<int> NewConns) MakeClient(int conns)
{
    var newConns = 0;
    var handler = new SocketsHttpHandler
    {
        MaxConnectionsPerServer = conns,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.None,
        UseProxy = opts.Proxy is not null,
        Proxy = opts.Proxy is null ? null : new WebProxy(opts.Proxy),
        ConnectCallback = async (ctx, ct) =>
        {
            Interlocked.Increment(ref newConns);
            var s = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await s.ConnectAsync(ctx.DnsEndPoint, ct);
                return new System.Net.Sockets.NetworkStream(s, ownsSocket: true);
            }
            catch
            {
                s.Dispose();
                throw;
            }
        },
    };
    var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    client.DefaultRequestVersion = HttpVersion.Version11;
    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("EpicGamesLauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");
    return (client, () => Volatile.Read(ref newConns));
}

async Task<(long Bytes, double Ttfb, double Total, bool Ok)> Fetch(HttpClient client, string url, byte[] buffer, CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    try
    {
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        var ttfb = sw.Elapsed.TotalMilliseconds;
        if (!resp.IsSuccessStatusCode)
        {
            return (0, ttfb, sw.Elapsed.TotalMilliseconds, false);
        }

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            Interlocked.Add(ref liveBytes, read);
        }

        return (total, ttfb, sw.Elapsed.TotalMilliseconds, true);
    }
    catch (Exception)
    {
        return (0, 0, sw.Elapsed.TotalMilliseconds, false);
    }
}

async Task<SweepResult> Sweep(string baseUrl, int conns, double seconds, double warmup)
{
    var (client, newConns) = MakeClient(conns);
    using var _ = client;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + warmup));
    var lat = new List<double>();
    var ttfbs = new List<double>();
    var errors = 0;
    var requests = 0;
    long measured = 0;
    Interlocked.Exchange(ref liveBytes, 0);
    var clock = Stopwatch.StartNew();
    var perSecond = new List<double>();

    var sampler = Task.Run(async () =>
    {
        long last = 0;
        while (!cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
            var now = Interlocked.Read(ref liveBytes);
            perSecond.Add((now - last) / 1e6);
            last = now;
        }
    });

    long warmBytes = -1;
    var warmTimer = Task.Delay(TimeSpan.FromSeconds(warmup)).ContinueWith(_ => warmBytes = Interlocked.Read(ref liveBytes));

    var workers = Enumerable.Range(0, conns).Select(async _ =>
    {
        var buffer = new byte[256 * 1024];
        while (!cts.IsCancellationRequested)
        {
            var i = Interlocked.Increment(ref cursor) % chunks.Length;
            var r = await Fetch(client, $"{baseUrl}/{chunks[i].Path}", buffer, cts.Token);
            if (cts.IsCancellationRequested)
            {
                break;
            }

            lock (lat)
            {
                requests++;
                if (!r.Ok) errors++;
                else if (clock.Elapsed.TotalSeconds > warmup)
                {
                    lat.Add(r.Total);
                    ttfbs.Add(r.Ttfb);
                }
            }
        }
    }).ToArray();

    await Task.WhenAll(workers);
    await sampler;
    await warmTimer;
    var total = Interlocked.Read(ref liveBytes);
    measured = total - Math.Max(0, warmBytes);
    lat.Sort();
    ttfbs.Sort();
    double Pct(List<double> l, double p) => l.Count == 0 ? 0 : l[Math.Min(l.Count - 1, (int)(l.Count * p))];
    return new SweepResult(measured / seconds / 1e6, Pct(lat, 0.5), Pct(lat, 0.95), Pct(ttfbs, 0.5), requests, errors, newConns(), total, perSecond.ToArray());
}

async Task<(double Ttfb, double Mbps, int Errors)> Probe(string baseUrl)
{
    // Warm-up GET of a small chunk opens 4 TLS connections (excluded), then 4 parallel 1 MiB GETs are timed.
    var (client, _) = MakeClient(4);
    using var __ = client;
    var small = chunks.Length > 0 ? Enumerable.Range(0, 4).Select(_ => chunks[Interlocked.Increment(ref cursor) % chunks.Length]).ToArray() : [];
    var buffer = new byte[256 * 1024];
    await Task.WhenAll(small.Select(c => Fetch(client, $"{baseUrl}/{c.Path}", new byte[65536], CancellationToken.None)));
    var sw = Stopwatch.StartNew();
    var timed = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
        Fetch(client, $"{baseUrl}/{chunks[Interlocked.Increment(ref cursor) % chunks.Length].Path}", new byte[256 * 1024], CancellationToken.None)));
    var secs = sw.Elapsed.TotalSeconds;
    var ok = timed.Where(t => t.Ok).ToArray();
    return (ok.Length == 0 ? 0 : ok.Average(t => t.Ttfb) / 1000, ok.Sum(t => t.Bytes) / secs / 1e6, timed.Length - ok.Length);
}

static string Host(string url) => new Uri(url).Host;

partial class Program
{
    static long liveBytes;
}

sealed record SweepResult(double Mbps, double P50, double P95, double Ttfb50, int Requests, int Errors, int NewConnections, long TotalBytes, double[] PerSecond);

sealed class Args
{
    public string Chunks = "chunks.json";
    public List<string> Bases = [];
    public int[] Conns = [1, 2, 4, 8, 16, 32, 64];
    public double Seconds = 8;
    public double Warmup = 2;
    public int Start = -1;
    public long BudgetMb = 4000;
    public string? Proxy;
    public bool Probe;
    public string? Json;

    public static Args Parse(string[] a)
    {
        var o = new Args();
        for (var i = 0; i < a.Length; i++)
        {
            string Next() => a[++i];
            switch (a[i])
            {
                case "--chunks": o.Chunks = Next(); break;
                case "--base": o.Bases.Add(Next().TrimEnd('/')); break;
                case "--conns": o.Conns = Next().Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--seconds": o.Seconds = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--warmup": o.Warmup = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--start": o.Start = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--budget-mb": o.BudgetMb = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--proxy": o.Proxy = Next(); break;
                case "--probe": o.Probe = true; break;
                case "--json": o.Json = Next(); break;
                default: throw new ArgumentException($"unknown argument {a[i]}");
            }
        }

        if (o.Bases.Count == 0)
        {
            throw new ArgumentException("--base is required");
        }

        return o;
    }
}
