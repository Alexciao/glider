using System.IO.Enumeration;
using System.Text.Json.Nodes;

namespace Glider.Core.Services;

/// <summary>
/// Real bytes on disk of the install folder (GameState.InstalledSizeBytes). Never runs on the caller's thread: a single
/// background worker measures, throttled (<see cref="GliderServiceOptions.SizeMeasureMinInterval"/>) and cancellable, and the
/// result is cached in cache/installed-size.json keyed by folder + version so a restart shows it immediately. Requests made
/// while a measurement runs coalesce into one follow-up.
/// </summary>
internal sealed class InstalledSizeMeter : IDisposable
{
    private readonly ServiceContext _c;
    private readonly string _cachePath;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop;
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private bool _pending;
    private bool _pendingForce;
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

    /// <summary>
    /// Ask for a (re)measurement; returns immediately. <paramref name="filesChanged"/> = an install/update/repair/verify/move
    /// just finished, so a cached value for the same folder and version no longer counts.
    /// </summary>
    public void Request(bool filesChanged)
    {
        lock (_gate)
        {
            _pending = true;
            _pendingForce |= filesChanged;
            if (_running)
            {
                return; // the running worker loops once more
            }

            _running = true;
            _worker = Task.Run(() => LoopAsync(_stop.Token));
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                bool force;
                lock (_gate)
                {
                    if (!_pending)
                    {
                        _running = false;
                        return;
                    }

                    _pending = false;
                    force = _pendingForce;
                    _pendingForce = false;
                }

                await MeasureOnceAsync(force, ct).ConfigureAwait(false);
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

    private async Task MeasureOnceAsync(bool force, CancellationToken ct)
    {
        if (_c.Records.Current is not { } rec)
        {
            return;
        }

        var cached = ReadCache(rec.InstallPath, rec.Version);
        if (cached is not null)
        {
            Publish(cached); // better than "not measured" while we re-walk
            if (!force && _c.Now - cached.MeasuredAt < _c.Options.SizeCacheMaxAge)
            {
                return;
            }
        }

        if (_lastFinished is { } last && _c.Now - last < _c.Options.SizeMeasureMinInterval)
        {
            await Task.Delay(_c.Options.SizeMeasureMinInterval - (_c.Now - last), _c.Time, ct).ConfigureAwait(false);
            if (_c.Records.Current is not { } again || again.InstallPath != rec.InstallPath || again.Version != rec.Version)
            {
                lock (_gate)
                {
                    _pending = true; // the install changed while we waited: measure what's there now
                }

                return;
            }
        }

        var measure = _c.Options.MeasureDirectory ?? SumFileLengths;
        var started = _c.Time.GetTimestamp();
        var bytes = measure(rec.InstallPath, ct);
        _lastFinished = _c.Now;
        _c.Log.Info("size", $"measured {rec.InstallPath}: {bytes} bytes in {_c.Time.GetElapsedTime(started).TotalSeconds:0.0} s");

        // The folder or version may have changed while we walked; a stale number must never be published.
        if (_c.Records.Current is { } now && now.InstallPath == rec.InstallPath && now.Version == rec.Version)
        {
            var m = new SizeMeasure(rec.InstallPath, rec.Version, bytes, _c.Now);
            Publish(m);
            await WriteCacheAsync(m).ConfigureAwait(false);
        }
        else
        {
            lock (_gate)
            {
                _pending = true;
            }
        }
    }

    private void Publish(SizeMeasure m) => _c.Hub.Mutate(s => s with { InstalledSize = m });

    private SizeMeasure? ReadCache(string path, string version)
    {
        var o = SmallFiles.TryReadObject(_cachePath);
        try
        {
            if (o is not null
                && (string?)o["path"] == path
                && (string?)o["version"] == version
                && (long?)o["bytes"] is { } bytes and >= 0
                && (DateTimeOffset?)o["measuredAt"] is { } at)
            {
                return new SizeMeasure(path, version, bytes, at);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
        }

        return null;
    }

    private async Task WriteCacheAsync(SizeMeasure m)
    {
        try
        {
            var o = new JsonObject { ["path"] = m.Path, ["version"] = m.Version, ["bytes"] = m.Bytes, ["measuredAt"] = m.MeasuredAt };
            await SmallFiles.WriteAtomicAsync(_cachePath, o.ToJsonString()).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _c.Log.Warn("size", "couldn't cache the installed size", e);
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
