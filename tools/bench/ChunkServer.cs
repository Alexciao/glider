// Local stand-in for Epic's chunk CDN with network fault injection, for benchmarking Legendary's pipeline without
// spending real bandwidth. Every GET for *.chunk returns the same real chunk file (Legendary does not check chunk
// hashes on download, it only parses the header, so the bytes written are realistic in size and incompressibility).
//
// Usage: dotnet run tools/bench/ChunkServer.cs -- --chunk one.chunk [--port 8099]
//          [--latency-ms 0] [--jitter-ms 0]      added before the response headers of every request (RTT / TTFB)
//          [--connect-ms 0]                      added once per new connection (TCP + TLS handshake, ~2-3 RTT)
//          [--conn-mbps 0]                       per-connection rate cap in MB/s (TCP per-flow limit under loss)
//          [--link-mbps 0]                       shared rate cap for all connections in MB/s (the Wi-Fi link)
//          [--reset-pct 0]                       % of responses whose connection is reset half-way through the body
//          [--stall-pct 0] [--stall-ms 3000]     % of responses that pause half-way through the body
//          [--stats stats.jsonl]                 one JSON line per second
// What it cannot emulate: real TCP congestion control (slow start, cwnd collapse after loss, retransmits), Wi-Fi
// airtime contention, bufferbloat that grows with the number of flows. --conn-mbps approximates the per-flow ceiling
// that loss + RTT impose (Mathis: rate ~ MSS / (RTT * sqrt(p))), --link-mbps the shared radio.
#:property PublishAot=false
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

var o = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2)
{
    o[args[i].TrimStart('-')] = args[i + 1];
}

double D(string k, double def) => o.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;
var body = File.ReadAllBytes(o["chunk"]);
var port = (int)D("port", 8099);
var latency = D("latency-ms", 0);
var jitter = D("jitter-ms", 0);
var connectMs = D("connect-ms", 0);
var connBps = D("conn-mbps", 0) * 1e6;
var linkBps = D("link-mbps", 0) * 1e6;
var resetPct = D("reset-pct", 0);
var stallPct = D("stall-pct", 0);
var stallMs = D("stall-ms", 3000);
var statsPath = o.GetValueOrDefault("stats");

long conns = 0, open = 0, maxOpen = 0, requests = 0, bytes = 0, resets = 0, stalls = 0;
var link = new TokenBucket(linkBps);
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start(1024);
Console.Error.WriteLine($"ChunkServer on http://127.0.0.1:{port} body={body.Length}B latency={latency}±{jitter}ms connect={connectMs}ms conn={connBps / 1e6}MB/s link={linkBps / 1e6}MB/s reset={resetPct}% stall={stallPct}%");

_ = Task.Run(async () =>
{
    long lastBytes = 0, lastReq = 0;
    while (true)
    {
        await Task.Delay(1000);
        var b = Interlocked.Read(ref bytes);
        var r = Interlocked.Read(ref requests);
        var line = string.Create(CultureInfo.InvariantCulture, $"{{\"ts\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"mbps\":{(b - lastBytes) / 1e6:F1},\"req_s\":{r - lastReq},\"open\":{Interlocked.Read(ref open)},\"max_open\":{Interlocked.Read(ref maxOpen)},\"conns\":{Interlocked.Read(ref conns)},\"resets\":{Interlocked.Read(ref resets)},\"stalls\":{Interlocked.Read(ref stalls)}}}");
        lastBytes = b;
        lastReq = r;
        if (statsPath is not null)
        {
            await File.AppendAllTextAsync(statsPath, line + "\n");
        }
    }
});

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => Serve(client));
}

async Task Serve(TcpClient client)
{
    Interlocked.Increment(ref conns);
    var now = Interlocked.Increment(ref open);
    long seen;
    while ((seen = Interlocked.Read(ref maxOpen)) < now && Interlocked.CompareExchange(ref maxOpen, now, seen) != seen)
    {
    }

    client.NoDelay = true;
    var s = client.GetStream();
    var buf = new byte[8192];
    try
    {
        if (connectMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(connectMs));
        }

        var pending = new StringBuilder();
        while (true)
        {
            // read one request head
            string head;
            while (true)
            {
                var text = pending.ToString();
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0)
                {
                    head = text[..end];
                    pending.Remove(0, end + 4);
                    break;
                }

                var n = await s.ReadAsync(buf);
                if (n == 0)
                {
                    return;
                }

                pending.Append(Encoding.ASCII.GetString(buf, 0, n));
            }

            Interlocked.Increment(ref requests);
            var path = head.Split(' ')[1];
            if (!path.EndsWith(".chunk", StringComparison.Ordinal))
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"));
                continue;
            }

            var delay = latency + (jitter > 0 ? (Random.Shared.NextDouble() * 2 - 1) * jitter : 0);
            if (delay > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay));
            }

            await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\nConnection: keep-alive\r\n\r\n"));
            var reset = resetPct > 0 && Random.Shared.NextDouble() * 100 < resetPct;
            var stall = stallPct > 0 && Random.Shared.NextDouble() * 100 < stallPct;
            var sw = Stopwatch.StartNew();
            const int Piece = 64 * 1024;
            for (var off = 0; off < body.Length; off += Piece)
            {
                if (off >= body.Length / 2 && (reset || stall))
                {
                    if (reset)
                    {
                        Interlocked.Increment(ref resets);
                        client.Client.LingerState = new LingerOption(true, 0);
                        client.Close(); // RST
                        return;
                    }

                    Interlocked.Increment(ref stalls);
                    stall = false;
                    await Task.Delay(TimeSpan.FromMilliseconds(stallMs));
                }

                var len = Math.Min(Piece, body.Length - off);
                await link.TakeAsync(len);
                if (connBps > 0)
                {
                    var due = (off + len) / connBps;
                    var ahead = due - sw.Elapsed.TotalSeconds;
                    if (ahead > 0.001)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(ahead));
                    }
                }

                await s.WriteAsync(body.AsMemory(off, len));
                Interlocked.Add(ref bytes, len);
            }
        }
    }
    catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
    {
    }
    finally
    {
        Interlocked.Decrement(ref open);
        client.Dispose();
    }
}

sealed class TokenBucket(double bytesPerSecond)
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _next; // seconds on _clock when the link is free again

    public async Task TakeAsync(int bytes)
    {
        if (bytesPerSecond <= 0)
        {
            return;
        }

        double wait;
        lock (_gate)
        {
            var now = _clock.Elapsed.TotalSeconds;
            _next = Math.Max(_next, now - 0.05) + bytes / bytesPerSecond; // allow a 50 ms burst
            wait = _next - now;
        }

        if (wait > 0.001)
        {
            await Task.Delay(TimeSpan.FromSeconds(wait));
        }
    }
}
