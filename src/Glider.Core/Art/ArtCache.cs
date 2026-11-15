using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glider.Core.Infrastructure;

namespace Glider.Core.Art;

/// <summary>Where a variant's picture comes from: an Epic key image URL and, when the catalog states it, its pixel width.</summary>
public sealed record ArtSource(string Url, int? Width);

/// <summary>The catalog side of the cache: resolves (app, variant) to the Epic URL the summary's art URL hash was computed from.</summary>
public interface IArtSource
{
    /// <summary>Null when the catalog has no such image (or hasn't been read yet).</summary>
    ArtSource? Resolve(string app, ArtKind kind);

    /// <summary>Completes once the catalog is readable (Legendary's files were parsed); used only on a cache miss.</summary>
    Task WhenReadyAsync(CancellationToken ct);
}

/// <summary>A cached image on disk, ready to stream.</summary>
public sealed record ArtFile(string Path, string ContentType, long Length);

/// <summary>What the host answers a <c>https://art.glider/…</c> request with.</summary>
public sealed record ArtResponse(int Status, ArtFile? File, string? CacheControl)
{
    public static ArtResponse NotFound { get; } = new(404, null, "no-store");
    public static ArtResponse BadRequest { get; } = new(400, null, "no-store");
}

/// <summary>A flat colour for blur-up, valid for one source image (its URL hash).</summary>
public sealed record ArtPlaceholder(string Hash, string Color);

public sealed record ArtCacheOptions
{
    /// <summary>
    /// Concurrent downloads. A Library screen asks for ~30 covers at once and a cold cache has hundreds to fetch; Epic's CDN is HTTP/2
    /// (many streams over one connection) and a 300 px cover is ~25 KB, so the limit is latency, not bandwidth: 6 made a cold screen
    /// wait in rounds (docs/perf.md, DECISIONS "art speed").
    /// </summary>
    public int MaxParallel { get; init; } = 16;

    /// <summary>How many of those slots background prefetching may hold at once: the rest stay free for what the screen asks for.</summary>
    public int PrefetchParallel { get; init; } = 3;

    /// <summary>Prefetching stops for the run after this many failures in a row (offline, a CDN outage): the next library change tries again.</summary>
    public int PrefetchGiveUpAfter { get; init; } = 8;

    /// <summary>Thumbnails on disk are trimmed (oldest use first) to 90 % of this. A 500-game library is ~100 MB.</summary>
    public long MaxBytes { get; init; } = 384L * 1024 * 1024;

    public int MaxImageBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>How long a request for an image whose source isn't known yet may wait for the catalog before it is a 404.</summary>
    public TimeSpan SourceWait { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>After a failed download the same image isn't retried for this long (offline → 404 at once).</summary>
    public TimeSpan FailureTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A cache hit refreshes the file's timestamp (the LRU key) at most this often.</summary>
    public TimeSpan TouchAfter { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Trim the folder after this many new files.</summary>
    public int PruneEveryWrites { get; init; } = 64;

    /// <summary>Placeholder colour changes reach the library at most this often (a cold library fetches hundreds of covers).</summary>
    public TimeSpan PlaceholderPublishDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>True when Epic is known to be unreachable: a miss is then a 404 without trying the network.</summary>
    public Func<bool>? IsOffline { get; init; }
}

/// <summary>
/// The lazy art cache behind <c>https://art.glider/…</c> (docs/v0.2-design.md §1, DECISIONS "art cache"): the first request for a
/// variant downloads Epic's picture (Epic's CDN resizes it: <c>?w=…&amp;resize=1</c>), stores it under
/// <c>cache/art/&lt;app&gt;/&lt;variant&gt;-&lt;hash of the source URL&gt;.jpg</c> and serves it; later requests (and later sessions) are
/// disk hits with no network. Concurrent requests for one file share one download, downloads are capped
/// (<see cref="ArtCacheOptions.MaxParallel"/>), failures are remembered briefly, the folder is trimmed LRU to a size cap and v0.1's
/// full-size <c>cache/art/&lt;sha16&gt;.jpg</c> files are adopted instead of downloaded again. When the host provides an
/// <see cref="IImageProcessor"/> the cache also learns each cover's colour (the summary's <c>Placeholder</c>) and shrinks images the
/// CDN did not resize.
/// </summary>
public sealed class ArtCache : IDisposable
{
    private const string Category = "art";
    private static readonly string[] Extensions = ["jpg", "png", "webp"];
    private static readonly System.Buffers.SearchValues<char> LowerHex = System.Buffers.SearchValues.Create("0123456789abcdef");

    private readonly string _root;
    private readonly IArtSource _source;
    private readonly IArtFetcher _fetcher;
    private readonly IImageProcessor? _images;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly ArtCacheOptions _options;
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentDictionary<string, Lazy<Task<ArtFile?>>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedUntil = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer _publishTimer;
    private readonly Lock _placeholderGate = new();
    private ImmutableDictionary<string, ArtPlaceholder> _placeholders = ImmutableDictionary<string, ArtPlaceholder>.Empty;
    private bool _placeholdersDirty;
    private bool _publishArmed;
    private readonly Lock _prefetchGate = new();
    private readonly Queue<(string App, ArtKind Kind)> _prefetchQueue = new();
    private readonly HashSet<string> _prefetchSeen = new(StringComparer.Ordinal);
    private int _prefetchWorkers;
    private int _prefetchFailures;
    private int _writesSincePrune;
    private int _pruning;
    private int _fetched;

    public ArtCache(string cacheRoot, IArtSource source, IArtFetcher fetcher, ILog log, TimeProvider time, ArtCacheOptions? options = null, IImageProcessor? images = null)
    {
        _root = Path.Combine(cacheRoot, "art");
        _source = source;
        _fetcher = fetcher;
        _log = log;
        _time = time;
        _options = options ?? new ArtCacheOptions();
        _images = images;
        _gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallel));
        _publishTimer = time.CreateTimer(_ => PublishPlaceholders(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The colour map changed (batched). Raised on a thread-pool thread.</summary>
    public event Action? PlaceholdersChanged;

    /// <summary>Downloads that completed this session (diagnostics / tests).</summary>
    public int Fetched => Volatile.Read(ref _fetched);

    /// <summary>Whether a decoder is available (colours are only learned with one).</summary>
    public bool CanAnalyze => _images is not null;

    private string PlaceholdersPath => Path.Combine(_root, "placeholders.json");

    // ------------------------------------------------------------------ placeholders

    /// <summary>Loads <c>cache/art/placeholders.json</c> (small, synchronous: needed before the first library build).</summary>
    public void LoadPlaceholders()
    {
        try
        {
            if (!File.Exists(PlaceholdersPath))
            {
                return;
            }

            var b = ImmutableDictionary.CreateBuilder<string, ArtPlaceholder>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(PlaceholdersPath));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object &&
                    p.Value.TryGetProperty("h", out var h) && h.GetString() is { Length: ArtUrls.HashLength } hash &&
                    p.Value.TryGetProperty("c", out var c) && c.GetString() is { Length: 7 } color && color[0] == '#')
                {
                    b[p.Name] = new ArtPlaceholder(hash, color);
                }
            }

            _placeholders = b.ToImmutable();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _log.Warn(Category, "art placeholders unreadable; they will be learned again", e);
        }
    }

    /// <summary>The learned colour for an app, only while it matches the current source image.</summary>
    public string? PlaceholderFor(string app, string hash) =>
        _placeholders.TryGetValue(app, out var p) && string.Equals(p.Hash, hash, StringComparison.Ordinal) ? p.Color : null;

    public IReadOnlyDictionary<string, ArtPlaceholder> Placeholders => _placeholders;

    private void SetPlaceholder(string app, string hash, string color)
    {
        lock (_placeholderGate)
        {
            if (_placeholders.TryGetValue(app, out var old) && old.Hash == hash && old.Color == color)
            {
                return;
            }

            _placeholders = _placeholders.SetItem(app, new ArtPlaceholder(hash, color));
            _placeholdersDirty = true;
            if (_publishArmed)
            {
                return;
            }

            _publishArmed = true;
        }

        var delay = _options.PlaceholderPublishDelay;
        if (delay <= TimeSpan.Zero)
        {
            _ = Task.Run(PublishPlaceholders);
        }
        else
        {
            _publishTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void PublishPlaceholders()
    {
        ImmutableDictionary<string, ArtPlaceholder> snapshot;
        lock (_placeholderGate)
        {
            _publishArmed = false;
            if (!_placeholdersDirty)
            {
                return;
            }

            _placeholdersDirty = false;
            snapshot = _placeholders;
        }

        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var (app, p) in snapshot.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    w.WriteStartObject(app);
                    w.WriteString("h", p.Hash);
                    w.WriteString("c", p.Color);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            AtomicFile.WriteAllBytesAsync(PlaceholdersPath, ms.ToArray()).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn(Category, "couldn't write cache/art/placeholders.json", e);
        }

        PlaceholdersChanged?.Invoke();
    }

    // ------------------------------------------------------------------ prefetch

    /// <summary>Pictures <see cref="Prefetch"/> found on disk or downloaded this session (diagnostics / tests).</summary>
    public int Prefetched => Volatile.Read(ref _prefetched);

    private int _prefetched;

    /// <summary>No prefetch download is running or waiting (tests).</summary>
    public bool PrefetchIdle
    {
        get
        {
            lock (_prefetchGate)
            {
                return _prefetchWorkers == 0 && _prefetchQueue.Count == 0;
            }
        }
    }

    /// <summary>
    /// Fills the cache ahead of the screen: each (game, variant) whose picture is not on disk is downloaded a few at a time, in the
    /// order given (the caller puts what the player is most likely to see first). Requests from the page share these downloads
    /// (one file, one download) and are never queued behind more than <see cref="ArtCacheOptions.PrefetchParallel"/> of them. Offline
    /// or after a run of failures it stops quietly; the next call starts again. Returns at once.
    /// </summary>
    public void Prefetch(IEnumerable<(string App, ArtKind Kind)> items)
    {
        var added = false;
        lock (_prefetchGate)
        {
            foreach (var item in items)
            {
                if (!ArtUrls.IsValidApp(item.App) || _source.Resolve(item.App, item.Kind) is not { } src)
                {
                    continue;
                }

                if (_prefetchSeen.Add($"{item.App}/{(int)item.Kind}/{ArtUrls.HashOf(src.Url)}"))
                {
                    _prefetchQueue.Enqueue(item);
                    added = true;
                }
            }

            if (!added || _stop.IsCancellationRequested)
            {
                return;
            }

            _prefetchFailures = 0;
            var want = Math.Max(1, Math.Min(_options.PrefetchParallel, _options.MaxParallel - 1)) - _prefetchWorkers;
            for (; want > 0; want--)
            {
                _prefetchWorkers++;
                _ = Task.Run(PrefetchWorkerAsync);
            }
        }
    }

    private async Task PrefetchWorkerAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                (string App, ArtKind Kind) item;
                lock (_prefetchGate)
                {
                    if (_options.IsOffline?.Invoke() == true || _prefetchFailures >= _options.PrefetchGiveUpAfter)
                    {
                        _prefetchQueue.Clear();
                        _prefetchSeen.Clear(); // the next library change starts over
                        return;
                    }

                    if (!_prefetchQueue.TryDequeue(out item))
                    {
                        return;
                    }
                }

                try
                {
                    var file = await GetAsync(item.App, item.Kind, null, _stop.Token).ConfigureAwait(false);
                    if (file is null)
                    {
                        Interlocked.Increment(ref _prefetchFailures);
                    }
                    else
                    {
                        Interlocked.Exchange(ref _prefetchFailures, 0);
                        Interlocked.Increment(ref _prefetched);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    _log.Debug(Category, $"prefetch of {item.App} failed: {e.GetType().Name}");
                    Interlocked.Increment(ref _prefetchFailures);
                }
            }
        }
        finally
        {
            lock (_prefetchGate)
            {
                _prefetchWorkers--;
                // Something was queued while the last worker was finishing: keep going.
                if (_prefetchQueue.Count > 0 && _prefetchWorkers == 0 && !_stop.IsCancellationRequested)
                {
                    _prefetchWorkers++;
                    _ = Task.Run(PrefetchWorkerAsync);
                }
            }
        }
    }

    // ------------------------------------------------------------------ requests

    /// <summary>Answers one <c>https://art.glider/…</c> request. Never throws; a miss that can't be filled is a quick 404.</summary>
    public async Task<ArtResponse> HandleAsync(string url, CancellationToken ct)
    {
        if (!ArtUrls.TryParse(url, out var app, out var kind, out var version))
        {
            return ArtResponse.BadRequest;
        }

        var file = await GetAsync(app, kind, version, ct).ConfigureAwait(false);
        return file is null
            ? ArtResponse.NotFound
            : new ArtResponse(200, file, version is null ? "public, max-age=3600" : "public, max-age=31536000, immutable");
    }

    /// <summary>The cached picture for a variant, fetching it first when needed. Null = none available (offline, no such art, bad image).</summary>
    public async Task<ArtFile?> GetAsync(string app, ArtKind kind, string? version, CancellationToken ct)
    {
        if (!ArtUrls.IsValidApp(app))
        {
            return null;
        }

        var spec = ArtSpec.For(kind);
        if (version is not null && FindCached(app, spec, version) is { } hit)
        {
            return hit;
        }

        var src = _source.Resolve(app, kind);
        if (src is null)
        {
            try
            {
                await _source.WhenReadyAsync(ct).WaitAsync(_options.SourceWait, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }

            src = _source.Resolve(app, kind);
        }

        if (src is null)
        {
            return null;
        }

        var hash = ArtUrls.HashOf(src.Url);
        if (version != hash && FindCached(app, spec, hash) is { } current)
        {
            return current; // the art changed since that URL was handed out: the current picture is the better answer
        }

        var key = $"{app}/{spec.Token}-{hash}";
        if (_failedUntil.TryGetValue(key, out var until))
        {
            if (until > _time.GetUtcNow())
            {
                return null;
            }

            _failedUntil.TryRemove(key, out _);
        }

        // One download per file: later requests join the first. The download itself is not cancelled by one caller leaving.
        var task = _inFlight.GetOrAdd(key, k => new Lazy<Task<ArtFile?>>(() => RunAsync(k, app, spec, src, hash))).Value;
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<ArtFile?> RunAsync(string key, string app, ArtSpec spec, ArtSource src, string hash)
    {
        try
        {
            var file = await FetchAsync(app, spec, src, hash, _stop.Token).ConfigureAwait(false);
            if (file is null)
            {
                _failedUntil[key] = _time.GetUtcNow() + _options.FailureTtl;
            }

            return file;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e)
        {
            _log.Debug(Category, $"art {key} unavailable: {e.GetType().Name} {e.Message}");
            _failedUntil[key] = _time.GetUtcNow() + _options.FailureTtl;
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<ArtFile?> FetchAsync(string app, ArtSpec spec, ArtSource src, string hash, CancellationToken ct)
    {
        var bytes = TryReadLegacy(src.Url);
        if (bytes is null)
        {
            if (_options.IsOffline?.Invoke() == true || !Uri.TryCreate(src.Url, UriKind.Absolute, out var source) || !ArtSourcePolicy.IsAllowed(source))
            {
                return null;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bytes = await _fetcher.FetchAsync(ArtSourcePolicy.RequestFor(source, spec.Width, src.Width), _options.MaxImageBytes, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            Interlocked.Increment(ref _fetched);
        }

        if (bytes is null || !ImageSniffer.TryGetKind(bytes, out var contentType, out var extension))
        {
            return null;
        }

        ImageFacts? facts = null;
        if (_images is not null && spec.Kind != ArtKind.Logo)
        {
            facts = _images.Analyze(bytes);
            if (spec.Resizes && facts is not null && facts.Width > spec.Width * 5 / 4 && _images.Downscale(bytes, spec.Width) is { } smaller && ImageSniffer.TryGetKind(smaller, out contentType, out extension))
            {
                bytes = smaller;
            }
        }

        var dir = Path.Combine(_root, app);
        var path = Path.Combine(dir, $"{spec.Token}-{hash}.{extension}");
        await AtomicFile.WriteAllBytesAsync(path, bytes, ct: ct).ConfigureAwait(false);
        RemoveOtherVersions(dir, spec, hash, path);

        if (facts is not null && spec.Kind != ArtKind.Logo)
        {
            var primary = _source.Resolve(app, ArtKind.TallThumb);
            if (primary is not null && ArtUrls.HashOf(primary.Url) == hash)
            {
                SetPlaceholder(app, hash, facts.DominantColor);
            }
        }

        if (Interlocked.Increment(ref _writesSincePrune) >= _options.PruneEveryWrites)
        {
            Interlocked.Exchange(ref _writesSincePrune, 0);
            _ = Task.Run(Prune);
        }

        return new ArtFile(path, contentType, bytes.Length);
    }

    private ArtFile? FindCached(string app, ArtSpec spec, string hash)
    {
        var dir = Path.Combine(_root, app);
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(dir, $"{spec.Token}-{hash}.{ext}");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                continue;
            }

            var type = ImageSniffer.ContentTypeOfExtension(ext);
            if (type is null)
            {
                continue;
            }

            var now = _time.GetUtcNow();
            if (now - info.LastWriteTimeUtc > _options.TouchAfter)
            {
                try
                {
                    info.LastWriteTimeUtc = now.UtcDateTime; // the LRU key
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            return new ArtFile(path, type, info.Length);
        }

        return null;
    }

    /// <summary>v0.1 stored the full-size original as <c>cache/art/&lt;first 16 hex of SHA-256(url)&gt;.&lt;ext&gt;</c>: adopt it instead of downloading again.</summary>
    private byte[]? TryReadLegacy(string sourceUrl)
    {
        try
        {
            var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl)))[..16];
            foreach (var ext in new[] { "jpg", "jpeg", "png", "webp" })
            {
                var path = Path.Combine(_root, $"{name}.{ext}");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0 && info.Length <= _options.MaxImageBytes)
                {
                    var bytes = File.ReadAllBytes(path);
                    return ImageSniffer.TryGetKind(bytes, out _, out _) ? bytes : null;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static void RemoveOtherVersions(string dir, ArtSpec spec, string hash, string keep)
    {
        try
        {
            var prefix = spec.Token + "-";
            foreach (var other in Directory.EnumerateFiles(dir, prefix + "*"))
            {
                var name = Path.GetFileNameWithoutExtension(other);
                if (name.Length != prefix.Length + ArtUrls.HashLength || string.Equals(other, keep, StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(hash, StringComparison.Ordinal) || name.AsSpan(prefix.Length).ContainsAnyExcept(LowerHex))
                {
                    continue; // another variant ("tall-thumb-…"), the current file, or not ours
                }

                File.Delete(other);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ LRU

    /// <summary>Total bytes of stored pictures.</summary>
    public long SizeOnDisk()
    {
        long total = 0;
        foreach (var f in Pictures())
        {
            total += f.Length;
        }

        return total;
    }

    private IEnumerable<FileInfo> Pictures()
    {
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                yield return new FileInfo(path);
            }
        }
    }

    /// <summary>Deletes the least recently used pictures until the folder is at 90 % of the cap. Returns the bytes freed.</summary>
    public long Prune()
    {
        if (Interlocked.Exchange(ref _pruning, 1) == 1)
        {
            return 0;
        }

        try
        {
            var files = Pictures().Where(f => f.Name.Contains('-', StringComparison.Ordinal) && !f.Name.StartsWith('.')).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= _options.MaxBytes)
            {
                return 0;
            }

            var target = _options.MaxBytes / 10 * 9;
            long freed = 0;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total - freed <= target)
                {
                    break;
                }

                try
                {
                    var len = f.Length;
                    f.Delete();
                    freed += len;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            _log.Info(Category, $"art cache trimmed: {freed / 1048576} MB freed, {(total - freed) / 1048576} MB left");
            return freed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        finally
        {
            Volatile.Write(ref _pruning, 0);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _publishTimer.Dispose();
        PublishPlaceholders();
        _gate.Dispose();
        _stop.Dispose();
    }
}
