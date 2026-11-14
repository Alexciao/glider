using System.IO.Enumeration;
using System.Text.Json.Nodes;

namespace Glider.Core.Services;

/// <summary>
/// Real bytes on disk of each install folder (GameSummary.InstalledSizeBytes). Never runs on the caller's thread: one
/// background worker measures queued apps one by one, throttled (<see cref="GliderServiceOptions.SizeMeasureMinInterval"/>
/// between walks) and cancellable. Results are cached per app in cache/installed-size.json keyed by folder + version, so a
/// restart shows them immediately. Repeated requests for an app coalesce.
/// </summary>
internal sealed class InstalledSizeMeter : IDisposable
{
    private readonly ServiceContext _c;
    private readonly string _cachePath;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop;
    private readonly Dictionary<string, bool> _pending = new(StringComparer.Ordinal);
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private DateTimeOffset? _lastFinished;

    public InstalledSizeMeter(ServiceContext context)
    {
        _c = context;
        _cachePath = Path.Combine(context.Paths.Cache, "installed-size.json");
        _stop = CancellationTokenSource.CreateLinkedTokenSource(context.Shutdown);
    }

    /// <summary>Completes when no measurement is running or queued (tests).</summary>
    public async Task IdleAsync()
    {
        while (true)
        {
            Task t;
            lock (_gate)
            {
                t = _worker;
            }

            await t.ConfigureAwait(false);
            lock (_gate)
            {
                if (t == _worker && !_running)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Publishes cached sizes (no walk) for every app in the cache file.</summary>
    public void LoadCached()
    {
        var all = ReadCacheFile();
        var b = _c.Hub.State.Sizes.ToBuilder();
        foreach (var (app, m) in all)
        {
            b[app] = m;
        }

        var sizes = b.ToImmutable();
        _c.Hub.Mutate(s => s with { Sizes = sizes });
    }

    /// <summary>
    /// Ask for a (re)measurement of one app; returns immediately. <paramref name="filesChanged"/> = an install/update/repair/
    /// verify/move just finished, so a cached value for the same folder and version no longer counts.
    /// </summary>
    public void Request(string app, bool filesChanged)
    {
        lock (_gate)
        {
            _pending[app] = (_pending.TryGetValue(app, out var f) && f) || filesChanged;
            if (_running)
            {
                return; // the running worker picks it up
            }

            _running = true;
            _worker = Task.Run(() => LoopAsync(_stop.Token));
        }
    }

    /// <summary>Every installed game (startup).</summary>
    public void RequestAll()
    {
        foreach (var g in _c.Library.Games.Values)
        {
            if (g.InstallPath is not null && g.InstalledVersion is not null && g.Kind == Contracts.GameKind.Game)
            {
                Request(g.AppName, filesChanged: false);
            }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                string app;
                bool force;
                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        _running = false;
                        return;
                    }

                    (app, force) = _pending.First();
                    _pending.Remove(app);
                }

                await MeasureOnceAsync(app, force, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _running = false;
            }
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                _running = false;
            }

            _c.Log.Warn("size", "installed-size measurement failed", e);
        }
    }

    /// <summary>Path and version of the install right now (installed.json read directly; Glider's record as a fallback).</summary>
    private async Task<(string Path, string Version)?> CurrentAsync(string app, CancellationToken ct)
    {
        var installed = await _c.Legendary.GetInstalledAsync(app, ct).ConfigureAwait(false);
        if (installed is { InstallPath.Length: > 0 })
        {
            return (installed.InstallPath, installed.Version);
        }

        var rec = _c.Games.Get(app).Install;
        return rec is null ? null : (rec.InstallPath, rec.Version);
    }

    private async Task MeasureOnceAsync(string app, bool force, CancellationToken ct)
    {
        if (await CurrentAsync(app, ct).ConfigureAwait(false) is not { } target)
        {
            return;
        }

        var cached = ReadCacheFile().GetValueOrDefault(app);
        if (cached is not null && cached.Path == target.Path && cached.Version == target.Version)
        {
            Publish(app, cached); // better than "not measured" while we re-walk
            if (!force && _c.Now - cached.MeasuredAt < _c.Options.SizeCacheMaxAge)
            {
                return;
            }
        }

        if (_lastFinished is { } last && _c.Now - last < _c.Options.SizeMeasureMinInterval)
        {
            await Task.Delay(_c.Options.SizeMeasureMinInterval - (_c.Now - last), _c.Time, ct).ConfigureAwait(false);
            if (await CurrentAsync(app, ct).ConfigureAwait(false) != target)
            {
                lock (_gate)
                {
                    _pending[app] = true; // the install changed while we waited: measure what's there now
                }

                return;
            }
        }

        var measure = _c.Options.MeasureDirectory ?? SumFileLengths;
        var started = _c.Time.GetTimestamp();
        var bytes = measure(target.Path, ct);
        _lastFinished = _c.Now;
        _c.Log.Info("size", $"measured {app} at {target.Path}: {bytes} bytes in {_c.Time.GetElapsedTime(started).TotalSeconds:0.0} s");

        // The folder or version may have changed while we walked; a stale number must never be published.
        if (await CurrentAsync(app, ct).ConfigureAwait(false) == target)
        {
            var m = new SizeMeasure(target.Path, target.Version, bytes, _c.Now);
            Publish(app, m);
            await WriteCacheAsync(app, m).ConfigureAwait(false);
        }
        else
        {
            lock (_gate)
            {
                _pending[app] = true;
            }
        }
    }

    private void Publish(string app, SizeMeasure m)
    {
        _c.Hub.Mutate(s => s.Sizes.TryGetValue(app, out var old) && old == m ? s : s with { Sizes = s.Sizes.SetItem(app, m) });
        _c.Library.Invalidate();
    }

    /// <summary>schema 2: <c>{"schemaVersion":2,"games":{"&lt;app&gt;":{path,version,bytes,measuredAt}}}</c>.</summary>
    private Dictionary<string, SizeMeasure> ReadCacheFile()
    {
        var result = new Dictionary<string, SizeMeasure>(StringComparer.Ordinal);
        var o = SmallFiles.TryReadObject(_cachePath);
        if (o?["games"] is not JsonObject games)
        {
            return result;
        }

        foreach (var (app, node) in games)
        {
            try
            {
                if (node is JsonObject g && (string?)g["path"] is { } path && (string?)g["version"] is { } version &&
                    (long?)g["bytes"] is { } bytes and >= 0 && (DateTimeOffset?)g["measuredAt"] is { } at)
                {
                    result[app] = new SizeMeasure(path, version, bytes, at);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException)
            {
            }
        }

        return result;
    }

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private async Task WriteCacheAsync(string app, SizeMeasure m)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var all = ReadCacheFile();
            all[app] = m;
            var games = new JsonObject();
            foreach (var (a, v) in all.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                games[a] = new JsonObject { ["path"] = v.Path, ["version"] = v.Version, ["bytes"] = v.Bytes, ["measuredAt"] = v.MeasuredAt };
            }

            await SmallFiles.WriteAtomicAsync(_cachePath, new JsonObject { ["schemaVersion"] = 2, ["games"] = games }.ToJsonString()).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _c.Log.Warn("size", "couldn't cache the installed size", e);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Sum of file lengths; skips reparse points (no junction loops) and anything unreadable.</summary>
    internal static long SumFileLengths(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var files = new FileSystemEnumerable<long>(path, static (ref FileSystemEntry e) => e.Length, options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry e) => !e.IsDirectory,
        };

        long total = 0;
        var n = 0;
        foreach (var length in files)
        {
            total += length;
            if ((++n & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
        }

        return total;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
