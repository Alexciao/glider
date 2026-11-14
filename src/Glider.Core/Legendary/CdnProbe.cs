using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glider.Core.Legendary;

/// <summary>One CDN host's probe result (docs/download-perf.md §5.1).</summary>
internal sealed record CdnHostResult(string BaseUrl, string Host, bool InEpicList, bool Probed, bool Failed, double TtfbMs, double MegabytesPerSecond)
{
    /// <summary>"ok", "slow", "failed" or "skipped" (never reached within the budget).</summary>
    public string Verdict { get; init; } = "skipped";
}

/// <summary>
/// The outcome of a job-start probe: hosts in Epic's order with verdicts, and the order to try them in. <see cref="Pick"/> is the
/// first host in Epic's order that isn't bad (the probe can't rank good hosts, only exclude bad ones: ±50 % between rounds).
/// </summary>
internal sealed class CdnPlan
{
    private readonly List<CdnHostResult> _order;

    public CdnPlan(IReadOnlyList<CdnHostResult> results)
    {
        Results = results;
        // Failover order: good hosts, then those the budget never reached, then bad ones (still better than looping on a dead host).
        _order = [.. results.Where(r => r.Verdict == "ok"), .. results.Where(r => r.Verdict == "skipped"), .. results.Where(r => r.Verdict is "slow" or "failed")];
    }

    public IReadOnlyList<CdnHostResult> Results { get; }

    public CdnHostResult? Pick => _order.Count > 0 && _order[0].Verdict == "ok" ? _order[0] : null;

    /// <summary>The next host to fail over to after <paramref name="currentHost"/> (never a bad one), or null.</summary>
    public CdnHostResult? Next(string? currentHost)
    {
        var i = currentHost is null ? -1 : _order.FindIndex(r => string.Equals(r.Host, currentHost, StringComparison.OrdinalIgnoreCase));
        return _order.Skip(i + 1).FirstOrDefault(r => r.Verdict is "ok" or "skipped");
    }

    public CdnHostResult? Find(string? host) => host is null ? null : _order.FirstOrDefault(r => string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>MB/s the probe measured on <paramref name="host"/> (the reference for "this CDN is slow now").</summary>
    public double? ProbeMegabytesPerSecond(string? host) => Find(host) is { Probed: true, Failed: false } r ? r.MegabytesPerSecond : null;
}

/// <summary>
/// Job-start CDN probe: per host (one after another — parallel probes split the link and bias the result), four warm keep-alive
/// connections with one untimed chunk GET each, then four parallel timed GETs of four other chunks. A host is bad if a request
/// failed, TTFB &gt; 250 ms or it moved &lt; 50 % of the best host's MB/s. About 4 MB and 0.3 s per host; 3 s per host and 2.5 s in
/// total at most.
/// </summary>
internal sealed partial class CdnProbe
{
    /// <summary>Hosts that serve the same <c>/Builds/…/CloudDir</c> tree as Epic's per-title list (measured 2026-11-14).</summary>
    internal static readonly string[] ExtraHosts = ["fastly-download.epicgames.com", "download.epicgames.com", "cloudflare.epicgamescdn.com"];

    internal const string UserAgent = "EpicGamesLauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

    private const double BadTtfbMs = 250;
    private const double BadShareOfBest = 0.5;

    private readonly Func<HttpMessageHandler> _handlers;
    private readonly ILog _log;

    public CdnProbe(ILog log, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _log = log;
        _handlers = handlerFactory ?? (() => new SocketsHttpHandler { MaxConnectionsPerServer = 8, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.None });
    }

    public TimeSpan HostTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan TotalBudget { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Epic's base URLs (in Epic's order) + the known extra hosts that share their path, as base URLs.</summary>
    public static IReadOnlyList<(string BaseUrl, bool InEpicList)> Candidates(IReadOnlyList<string> epicBaseUrls)
    {
        var list = new List<(string, bool)>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? path = null;
        foreach (var u in epicBaseUrls)
        {
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && hosts.Add(uri.Host))
            {
                list.Add((u.TrimEnd('/'), true));
                if (path is null && CloudDirPath().IsMatch(uri.AbsolutePath))
                {
                    path = uri.AbsolutePath.TrimEnd('/');
                }
            }
        }

        if (path is not null)
        {
            foreach (var host in ExtraHosts)
            {
                if (hosts.Add(host))
                {
                    list.Add(($"https://{host}{path}", false));
                }
            }
        }

        return list;
    }

    public async Task<CdnPlan?> ProbeAsync(IReadOnlyList<string> epicBaseUrls, IReadOnlyList<string> chunkPaths, CancellationToken ct)
    {
        var candidates = Candidates(epicBaseUrls);
        if (candidates.Count < 2 || chunkPaths.Count < 2)
        {
            return null; // nothing to choose between
        }

        var warmCount = Math.Min(4, chunkPaths.Count / 2);
        var timedCount = Math.Min(4, chunkPaths.Count - warmCount);
        var warm = chunkPaths.Take(warmCount).ToArray();
        var timed = chunkPaths.Skip(warmCount).Take(timedCount).ToArray();

        using var client = new HttpClient(_handlers(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        var started = Stopwatch.GetTimestamp();
        var raw = new List<RawResult>();
        foreach (var (baseUrl, epic) in candidates)
        {
            // Out of time, or three hosts measured and one of them already passes: the rest is "skipped" (usable as failover, never preferred).
            if (Stopwatch.GetElapsedTime(started) > TotalBudget || (raw.Count(r => r.Probed) >= 3 && Verdicts(raw).Contains("ok")))
            {
                raw.Add(new RawResult(baseUrl, epic, false, false, 0, 0));
                continue;
            }

            using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hostCts.CancelAfter(HostTimeout);
            var result = await ProbeHostAsync(client, baseUrl, warm, timed, hostCts.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            raw.Add(new RawResult(baseUrl, epic, true, result.Failed, result.TtfbMs, result.Mbps));
        }

        var verdicts = Verdicts(raw);
        var results = raw.Select((r, i) => new CdnHostResult(r.BaseUrl, new Uri(r.BaseUrl).Host, r.Epic, r.Probed, r.Failed, r.Ttfb, r.Mbps) { Verdict = verdicts[i] }).ToList();

        var plan = new CdnPlan(results);
        foreach (var r in results)
        {
            _log.Info("cdn", $"cdn probe: host={r.Host} ttfb={r.TtfbMs.ToString("0", CultureInfo.InvariantCulture)} ms mbps={r.MegabytesPerSecond.ToString("0.0", CultureInfo.InvariantCulture)} verdict={r.Verdict}{(r.InEpicList ? "" : " (extra host)")}");
        }

        _log.Info("cdn", $"cdn probe: pick={plan.Pick?.Host ?? "none"} ({Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms)");
        return plan;
    }

    private readonly record struct RawResult(string BaseUrl, bool Epic, bool Probed, bool Failed, double Ttfb, double Mbps);

    private static List<string> Verdicts(List<RawResult> raw)
    {
        var best = raw.Where(r => r is { Probed: true, Failed: false }).Select(r => r.Mbps).DefaultIfEmpty(0).Max();
        return [.. raw.Select(r => !r.Probed ? "skipped"
            : r.Failed ? "failed"
            : r.Ttfb > BadTtfbMs || r.Mbps < BadShareOfBest * best ? "slow"
            : "ok")];
    }

    private static async Task<(bool Failed, double TtfbMs, double Mbps)> ProbeHostAsync(HttpClient client, string baseUrl, string[] warm, string[] timed, CancellationToken ct)
    {
        try
        {
            // Untimed: opens the connections (TCP + TLS) the timed requests will reuse, like a Legendary worker's keep-alive session.
            await Task.WhenAll(warm.Select(p => FetchAsync(client, $"{baseUrl}/{p}", ct))).ConfigureAwait(false);
            var wall = Stopwatch.GetTimestamp();
            var fetched = await Task.WhenAll(timed.Select(p => FetchAsync(client, $"{baseUrl}/{p}", ct))).ConfigureAwait(false);
            var seconds = Math.Max(1e-3, Stopwatch.GetElapsedTime(wall).TotalSeconds);
            var bytes = fetched.Sum(f => f.Bytes);
            return (false, fetched.Average(f => f.TtfbMs), bytes / seconds / 1_000_000.0);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return (true, 0, 0);
        }
    }

    private static async Task<(long Bytes, double TtfbMs)> FetchAsync(HttpClient client, string url, CancellationToken ct)
    {
        var t0 = Stopwatch.GetTimestamp();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var ttfb = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
            }

            if (total == 0)
            {
                throw new HttpRequestException("empty chunk");
            }

            return (total, ttfb);
        }
    }

    [GeneratedRegex(@"^/Builds/[^/]+/CloudDir/?$", RegexOptions.IgnoreCase)]
    private static partial Regex CloudDirPath();

    /// <summary>Base URLs Legendary saved for the title: metadata/&lt;app&gt;.json first, installed.json as fallback. Empty when unknown.</summary>
    public static IReadOnlyList<string> ReadBaseUrls(string legendaryConfig, string appName)
    {
        foreach (var file in new[] { Path.Combine(legendaryConfig, "metadata", appName + ".json"), Path.Combine(legendaryConfig, "installed.json") })
        {
            try
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { AllowTrailingCommas = true });
                var root = doc.RootElement;
                if (Path.GetFileName(file) == "installed.json")
                {
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(appName, out root))
                    {
                        continue;
                    }
                }

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("base_urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                {
                    var list = urls.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToList();
                    if (list.Count > 0)
                    {
                        return list;
                    }
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // Rewritten in place by Legendary, or unreadable: try the next source.
            }
        }

        return [];
    }

    /// <summary>The newest manifest Legendary cached for <paramref name="appName"/> (<c>manifests/&lt;app&gt;_*.manifest</c>), or null.</summary>
    public static string? FindManifest(string legendaryConfig, string appName)
    {
        var dir = Path.Combine(legendaryConfig, "manifests");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        try
        {
            return new DirectoryInfo(dir).EnumerateFiles(appName + "_*.manifest")
                .Where(f => !f.Name.Contains(".delta", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
