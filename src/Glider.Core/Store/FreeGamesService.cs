using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;

namespace Glider.Core.Store;

/// <summary>Tunables and test seams of <see cref="FreeGamesService"/>.</summary>
public sealed class FreeGamesOptions
{
    public const string DefaultEndpoint = "https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions";

    /// <summary>Null = a real <see cref="HttpClientHandler"/>.</summary>
    public HttpMessageHandler? Handler { get; init; }

    public string Endpoint { get; init; } = DefaultEndpoint;

    /// <summary>ISO 3166-1 alpha-2, upper case. Null = the OS region.</summary>
    public string? Country { get; init; }

    /// <summary>Store path locale. Null = from the UI culture.</summary>
    public string? Locale { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Refresh when the cache is older than this (docs §5.4: 1 h)…</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(1);

    /// <summary>…or this long after a promotion window opens/closes, whichever is first (so the strip flips right at 16:00 UTC).</summary>
    public TimeSpan BoundarySlack { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Fetches are never closer together than this (manual refresh included unless forced).</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Older than this (or the last fetch failed) → <see cref="FreeGamesResult.Stale"/>.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The first background fetch waits this long (after first paint).</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Download art into the cache (off in tests that only look at the data).</summary>
    public bool DownloadArt { get; init; } = true;

    /// <summary>Responses larger than this are rejected (the real feed is ~50 KB).</summary>
    public int MaxBodyBytes { get; init; } = 2 * 1024 * 1024;

    public int MaxImageBytes { get; init; } = 2 * 1024 * 1024;
}

/// <summary>When the feed is next due. Pure so it can be tested without clocks.</summary>
public static class FreeGamesSchedule
{
    /// <summary>
    /// The earliest time a refresh is due: <c>fetchedAt + ttl</c>, pulled forward to <c>boundary + slack</c> for any promotion
    /// edge that falls inside that span, then pushed back by the minimum spacing and by the failure backoff.
    /// </summary>
    public static DateTimeOffset NextDue(
        DateTimeOffset? fetchedAt,
        IReadOnlyList<DateTimeOffset> boundaries,
        DateTimeOffset? lastAttempt,
        int failures,
        FreeGamesOptions o)
    {
        DateTimeOffset due;
        if (fetchedAt is null)
        {
            due = DateTimeOffset.MinValue;
        }
        else
        {
            due = fetchedAt.Value + o.Ttl;
            foreach (var b in boundaries)
            {
                var t = b + o.BoundarySlack;
                if (t > fetchedAt.Value && t < due)
                {
                    due = t;
                }
            }
        }

        if (lastAttempt is { } last)
        {
            var floor = last + Spacing(failures, o);
            if (due < floor)
            {
                due = floor;
            }
        }

        return due;
    }

    /// <summary>Minimum gap after an attempt: the 5-minute floor, or the failure backoff (1 → 2 → 4 → 8 → 15 min) when that is longer.</summary>
    public static TimeSpan Spacing(int failures, FreeGamesOptions o)
    {
        if (failures <= 0)
        {
            return o.MinInterval;
        }

        var backoff = TimeSpan.FromMinutes(Math.Min(o.MaxBackoff.TotalMinutes, Math.Pow(2, Math.Min(failures - 1, 10))));
        return backoff > o.MinInterval ? backoff : o.MinInterval;
    }

    /// <summary>The next moment the *rendered* result changes without a fetch: the nearest window edge after <paramref name="now"/>.</summary>
    public static DateTimeOffset? NextBoundary(DateTimeOffset now, IReadOnlyList<DateTimeOffset> boundaries) =>
        boundaries.Where(b => b > now).Cast<DateTimeOffset?>().Min();
}

/// <summary>
/// Epic's public free-games feed (docs/research/store-webview.md §5): fetch from Core (never from a web page), keep the last good
/// body on disk, re-parse it against "now" on every read so the strip flips on time even offline, and cache the art in
/// <c>cache/free-games/</c> (served at https://cache.glider/). Never claims anything.
/// </summary>
internal sealed class FreeGamesService : IDisposable
{
    private const string Category = "freegames";

    private readonly AppPaths _paths;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly Func<IReadOnlyCollection<GameSummary>> _library;
    private readonly IEventSink? _sink;
    private readonly FreeGamesOptions _o;
    private readonly string _country;
    private readonly string _locale;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly SemaphoreSlim _imageSlots = new(3, 3);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _imagesInFlight = [];

    private string? _body;
    private DateTimeOffset? _fetchedAt;
    private DateTimeOffset? _lastAttempt;
    private int _failures;
    private bool _lastFailed;
    private string? _lastPublishedKey;
    private bool _loaded;
    private Task _loop = Task.CompletedTask;

    public FreeGamesService(
        AppPaths paths,
        ILog log,
        TimeProvider time,
        Func<IReadOnlyCollection<GameSummary>> library,
        IEventSink? sink = null,
        FreeGamesOptions? options = null)
    {
        _paths = paths;
        _log = log;
        _time = time;
        _library = library;
        _sink = sink;
        _o = options ?? new FreeGamesOptions();
        _country = (_o.Country ?? RegionCode()).ToUpperInvariant();
        _locale = _o.Locale ?? StoreLocale.FromCurrentCulture();
        _http = new HttpClient(_o.Handler ?? new HttpClientHandler { UseCookies = false, UseProxy = true }, disposeHandler: _o.Handler is null)
        {
            Timeout = _o.RequestTimeout,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Glider"); // generic: nothing about the user or the machine
    }

    public string Country => _country;

    public string Locale => _locale;

    private DateTimeOffset Now => _time.GetUtcNow();

    private string CacheFile => Path.Combine(_paths.Cache, $"free-games.{_country}.{_locale}.json");

    private string ArtDir => Path.Combine(_paths.Cache, "free-games");

    // ------------------------------------------------------------------ public surface

    /// <summary>Starts the background loop (cache first, then refreshes as scheduled). Returns at once.</summary>
    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>The current result from the cache, rendered for "now". Never touches the network.</summary>
    public FreeGamesResult Get()
    {
        EnsureLoaded();
        return Render();
    }

    /// <summary>Fetches now (spacing applies unless <paramref name="force"/>), then returns the result. Offline → the cache.</summary>
    public async Task<FreeGamesResult> RefreshAsync(bool force, CancellationToken ct)
    {
        EnsureLoaded();
        await FetchAsync(force, ct).ConfigureAwait(false);
        return Render();
    }

    /// <summary>Re-publish with the library's current ownership (a purchase landed).</summary>
    public void Republish() => PublishIfChanged();

    // ------------------------------------------------------------------ loop

    internal async Task RunAsync(CancellationToken ct)
    {
        try
        {
            EnsureLoaded();
            PublishIfChanged();
            await Task.Delay(_o.StartupDelay, _time, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var now = Now;
                DateTimeOffset due;
                IReadOnlyList<DateTimeOffset> boundaries;
                lock (_gate)
                {
                    boundaries = Boundaries(now);
                    due = FreeGamesSchedule.NextDue(_fetchedAt, boundaries, _lastAttempt, _failures, _o);
                }

                if (due <= now)
                {
                    await FetchAsync(force: false, ct).ConfigureAwait(false);
                    PublishIfChanged();
                    lock (_gate)
                    {
                        boundaries = Boundaries(Now);
                        due = FreeGamesSchedule.NextDue(_fetchedAt, boundaries, _lastAttempt, _failures, _o);
                    }
                }

                // Wake for the next fetch OR the next window edge (the rendered strip changes then even without a fetch).
                var wake = due;
                if (FreeGamesSchedule.NextBoundary(Now, boundaries) is { } edge && edge < wake)
                {
                    wake = edge;
                }

                var delay = wake - Now;
                if (delay < TimeSpan.FromSeconds(1))
                {
                    delay = TimeSpan.FromSeconds(1);
                }

                if (delay > TimeSpan.FromHours(6))
                {
                    delay = TimeSpan.FromHours(6);
                }

                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                PublishIfChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.Warn(Category, "free games loop stopped", e);
        }
    }

    // ------------------------------------------------------------------ fetch

    private async Task FetchAsync(bool force, CancellationToken ct)
    {
        if (!await _fetchLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            // Single flight: someone is already fetching; wait for it and use its result.
            await _fetchLock.WaitAsync(ct).ConfigureAwait(false);
            _fetchLock.Release();
            return;
        }

        try
        {
            var now = Now;
            lock (_gate)
            {
                if (!force && _lastAttempt is { } last && now < last + FreeGamesSchedule.Spacing(_failures, _o))
                {
                    return;
                }

                _lastAttempt = now;
            }

            var url = $"{_o.Endpoint}?locale={Uri.EscapeDataString(_locale)}&country={Uri.EscapeDataString(_country)}&allowCountries={Uri.EscapeDataString(_country)}";
            try
            {
                using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                res.EnsureSuccessStatusCode();
                var bytes = await ReadBoundedAsync(res.Content, _o.MaxBodyBytes, ct).ConfigureAwait(false);
                var body = Encoding.UTF8.GetString(bytes);
                var parsed = FreeGamesParser.Parse(body, Now);
                if (!LooksLikeFeed(body))
                {
                    throw new InvalidDataException("not a promotions feed");
                }

                lock (_gate)
                {
                    _body = body;
                    _fetchedAt = Now;
                    _failures = 0;
                    _lastFailed = false;
                }

                await SaveCacheAsync(body, ct).ConfigureAwait(false);
                _log.Info(Category, $"free games refreshed: {parsed.Now.Count} now, {parsed.Soon.Count} soon");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or IOException && !ct.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _failures++;
                    _lastFailed = true;
                }

                _log.Warn(Category, $"free games fetch failed ({e.GetType().Name}); serving the cache");
            }
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private static bool LooksLikeFeed(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int max, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } len && len > max)
        {
            throw new InvalidDataException("response too large");
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > max)
            {
                throw new InvalidDataException("response too large");
            }
        }

        return ms.ToArray();
    }

    // ------------------------------------------------------------------ disk cache

    private sealed class CacheFileModel
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTimeOffset FetchedAt { get; set; }
        public string Body { get; set; } = "";
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
        }

        try
        {
            if (!File.Exists(CacheFile))
            {
                return;
            }

            var model = JsonSerializer.Deserialize<CacheFileModel>(File.ReadAllText(CacheFile, Encoding.UTF8));
            if (model is { Body.Length: > 0 } && LooksLikeFeed(model.Body))
            {
                lock (_gate)
                {
                    _body ??= model.Body;
                    _fetchedAt ??= model.FetchedAt;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warn(Category, "free games cache unreadable; ignoring it", e);
        }
    }

    private async Task SaveCacheAsync(string body, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_paths.Cache);
            var model = new CacheFileModel { FetchedAt = Now, Body = body };
            await AtomicFile.WriteAllTextAsync(CacheFile, JsonSerializer.Serialize(model), ct: ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn(Category, "free games cache not written", e);
        }
    }

    // ------------------------------------------------------------------ render

    private IReadOnlyList<DateTimeOffset> Boundaries(DateTimeOffset now) =>
        _body is null ? [] : FreeGamesParser.Parse(_body, now).Boundaries;

    private FreeGamesResult Render()
    {
        string? body;
        DateTimeOffset? fetchedAt;
        bool failed;
        lock (_gate)
        {
            body = _body;
            fetchedAt = _fetchedAt;
            failed = _lastFailed;
        }

        if (body is null)
        {
            return new FreeGamesResult([], null);
        }

        var now = Now;
        var parsed = FreeGamesParser.Parse(body, now);
        var owned = OwnedKeys();
        var items = new List<FreeGame>(parsed.Now.Count + parsed.Soon.Count);
        foreach (var offer in parsed.Now.Concat(parsed.Soon))
        {
            items.Add(Map(offer, owned));
        }

        var stale = failed || fetchedAt is null || now - fetchedAt.Value > _o.StaleAfter;
        return new FreeGamesResult(items, fetchedAt, stale);
    }

    private FreeGame Map(FreeOffer o, HashSet<string> owned)
    {
        var isOwned = o.ItemIds.Count > 0 && o.ItemIds.All(id => owned.Contains(o.Namespace + "|" + id));
        return new FreeGame(
            o.OfferId,
            o.Title,
            ArtUrl(o.WideImage, FreeGamesParser.WideResize),
            o.StoreSlug,
            o.StartsAt,
            o.EndsAt,
            o.Status,
            isOwned,
            ArtUrl(o.TallImage, FreeGamesParser.TallResize),
            o.RegularPrice,
            o.Seller,
            o.IsBundle);
    }

    private HashSet<string> OwnedKeys()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var g in _library())
            {
                if (g.Namespace.Length > 0 && g.CatalogItemId.Length > 0)
                {
                    set.Add(g.Namespace + "|" + g.CatalogItemId);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // library mutated while enumerating: a fresh render follows
        }

        return set;
    }

    // ------------------------------------------------------------------ art

    /// <summary>The cached copy when it exists (and a download is started in the background otherwise); the CDN URL until then.</summary>
    private string? ArtUrl(string? original, string resize)
    {
        if (original is null)
        {
            return null;
        }

        var cdn = original + resize;
        if (!_o.DownloadArt)
        {
            return cdn;
        }

        var name = ArtFileName(cdn);
        var path = Path.Combine(ArtDir, name);
        if (File.Exists(path))
        {
            return WebHost.CacheUrl("free-games/" + name);
        }

        StartDownload(cdn, path);
        return cdn;
    }

    internal static string ArtFileName(string cdnUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(cdnUrl));
        return Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant() + ".img";
    }

    private void StartDownload(string cdnUrl, string path)
    {
        lock (_gate)
        {
            if (!_imagesInFlight.Add(cdnUrl) || _cts.IsCancellationRequested)
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            var ct = _cts.Token;
            try
            {
                await _imageSlots.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var res = await _http.GetAsync(cdnUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    res.EnsureSuccessStatusCode();
                    var bytes = await ReadBoundedAsync(res.Content, _o.MaxImageBytes, ct).ConfigureAwait(false);
                    if (!LooksLikeImage(bytes))
                    {
                        return;
                    }

                    Directory.CreateDirectory(ArtDir);
                    await AtomicFile.WriteAllBytesAsync(path, bytes, ct: ct).ConfigureAwait(false);
                }
                finally
                {
                    _imageSlots.Release();
                }

                PublishIfChanged();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // The CDN URL keeps working; the next render retries.
            }
            finally
            {
                lock (_gate)
                {
                    _imagesInFlight.Remove(cdnUrl);
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>JPEG/PNG/WebP/GIF magic: refuses an HTML error page saved as an image.</summary>
    internal static bool LooksLikeImage(ReadOnlySpan<byte> b) =>
        b.Length > 12 && (
            (b[0] == 0xFF && b[1] == 0xD8) ||
            (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
            (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) ||
            (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50));

    // ------------------------------------------------------------------ events

    private void PublishIfChanged()
    {
        if (_sink is null)
        {
            return;
        }

        var result = Render();
        var key = string.Join('|', result.Items.Select(i => $"{i.Id}:{i.Status}:{i.Owned}:{i.ImageUrl}")) + "|" + result.Stale + "|" + result.FetchedAt?.ToUnixTimeSeconds();
        lock (_gate)
        {
            if (key == _lastPublishedKey)
            {
                return;
            }

            _lastPublishedKey = key;
        }

        _sink.Publish(RpcEvents.FreeGames, result);
    }

    private static string RegionCode()
    {
        try
        {
            var code = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return code.Length == 2 && code.All(char.IsLetter) ? code : "US";
        }
        catch (Exception e) when (e is ArgumentException or CultureNotFoundException)
        {
            return "US";
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }
}
