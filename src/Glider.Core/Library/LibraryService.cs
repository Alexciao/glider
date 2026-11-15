using System.Collections.Immutable;
using System.Text.Json;
using Glider.Core.Art;
using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Infrastructure;
using Glider.Core.Services;

namespace Glider.Core.Library;

/// <summary>cache/library.json: the last published summaries, so a cold start shows the library before any file is parsed.</summary>
public sealed class LibraryCacheFile
{
    public int SchemaVersion { get; set; } = 2;

    public DateTimeOffset SavedAt { get; set; }

    public List<GameSummary> Games { get; set; } = [];
}

/// <summary>
/// The library (docs/v0.2-design.md §1-2): one immutable <see cref="GameSummary"/> map + a revision. Any change to any game
/// produces one coalesced <see cref="LibraryDelta"/> with only the changed summaries. Served from cache/library.json at cold
/// start (no Legendary process, no parse of Legendary's files), then rebuilt from Legendary's files and Glider's records.
/// Background sync (`list -T --json`) and update checks (`list-installed --check-updates`) run on the metadata lane; the
/// UI never waits for them.
/// </summary>
internal sealed class LibraryService : IArtSource, IDisposable
{
    private const string Category = "library";

    /// <summary>The longest a foreground flush (library.get, game.getDetail) waits for a rebuild that is already running.</summary>
    private static readonly TimeSpan FlushTimeBox = TimeSpan.FromMilliseconds(500);

    /// <summary>With no cached library at all, library.get has nothing to show until Legendary's files are parsed: it waits that long for them.</summary>
    private static readonly TimeSpan FirstReadTimeBox = TimeSpan.FromSeconds(2);

    /// <summary>cache/library.json is rewritten at most this often (after the quiet delay), and only when a summary changed since the last write.</summary>
    private static readonly TimeSpan MinCacheWriteInterval = TimeSpan.FromSeconds(30);

    private readonly ServiceContext _c;
    private readonly LegendaryLibraryReader _reader;
    private readonly string _cachePath;
    private readonly string _artIndexPath;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _rebuild = new(1, 1);
    private readonly ITimer _flushTimer;
    private readonly ITimer _cacheTimer;
    private readonly HashSet<(string App, string Installed, string Latest)> _sizeTried = [];
    private LegendaryLibrary _lib = LegendaryLibrary.Empty;
    private bool _filesRead;
    private readonly TaskCompletionSource _filesReadSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ImmutableDictionary<string, GameSummary> _published = ImmutableDictionary<string, GameSummary>.Empty;
    private ImmutableDictionary<string, GameArt> _art = ImmutableDictionary<string, GameArt>.Empty;
    private long _revision;
    private bool _pendingRebuild;
    private bool _pendingReread;
    private bool _flushArmed;
    private readonly CatalogMemo _memo = new();
    private readonly SemaphoreSlim _cacheWrite = new(1, 1);
    private bool _cacheDirty;
    private bool _cacheArmed;
    private DateTimeOffset _lastCacheWrite = DateTimeOffset.MinValue;
    private long _cacheWrites;
    private GameSummary[]? _sorted;
    private long _sortedRevision = -1;
    private bool _disposed;
    private Task _syncTask = Task.CompletedTask;
    private Task _sizesTask = Task.CompletedTask;
    private bool _sizesRerun; // a rebuild asked while a measurement ran: look again once it finishes

    public LibraryService(ServiceContext context)
    {
        _c = context;
        _reader = new LegendaryLibraryReader(context.Paths.LegendaryConfig, context.Log, context.Options.LibraryRetryDelay);
        _cachePath = PathFor(context.Paths);
        _artIndexPath = Path.Combine(context.Paths.Cache, "art", "index.json");
        _flushTimer = context.Time.CreateTimer(_ => _ = FlushAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _cacheTimer = context.Time.CreateTimer(_ => _ = SaveCacheAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static string PathFor(AppPaths paths) => Path.Combine(paths.Cache, "library.json");

    /// <summary>Raised after a rebuild changed any game's update availability (auto-update and notices react).</summary>
    public event Action? UpdatesChanged;

    public long Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    public ImmutableDictionary<string, GameSummary> Games
    {
        get
        {
            lock (_gate)
            {
                return _published;
            }
        }
    }

    public LegendaryLibrary Files
    {
        get
        {
            lock (_gate)
            {
                return _lib;
            }
        }
    }

    /// <summary>Legendary's files were parsed at least once this session (before that, only the cache is known).</summary>
    public bool FilesRead
    {
        get
        {
            lock (_gate)
            {
                return _filesRead;
            }
        }
    }

    public GameSummary? Summary(string app) => Games.TryGetValue(app, out var s) ? s : null;

    public CatalogItem? CatalogItem(string app) => Files.Items.TryGetValue(app, out var i) ? i : null;

    // IArtSource: the art cache resolves a variant's Epic URL from the catalog on a cache miss.
    ArtSource? IArtSource.Resolve(string app, ArtKind kind) => CatalogItem(app) is { } item ? GameCatalog.ArtSourceOf(item, kind) : null;

    Task IArtSource.WhenReadyAsync(CancellationToken ct) => _filesReadSignal.Task.WaitAsync(ct);

    public LegendaryInstalledGame? Installed(string app) => Files.Installed?.GetValueOrDefault(app);

    /// <summary>Catalog FolderName (Legendary installs into &lt;base&gt;\&lt;FolderName&gt;), falling back to the app name.</summary>
    public string FolderName(string app) => CatalogItem(app)?.FolderName is { Length: > 0 } f && f.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? f : app;

    /// <summary>Completes when the current background sync / update check finished (tests).</summary>
    internal Task SyncTask => _syncTask;

    /// <summary>Completes when the current update-size measurements finished (tests).</summary>
    internal Task SizesTask => _sizesTask;

    /// <summary>Summaries the last rebuild computed vs. took over unchanged (tests).</summary>
    internal (int Rebuilt, int Reused) LastBuild => (_memo.Rebuilt, _memo.Reused);

    /// <summary>How many times cache/library.json was written this session (tests).</summary>
    internal long CacheWrites => Interlocked.Read(ref _cacheWrites);

    public LibraryStatus Status()
    {
        var s = _c.Hub.State;
        var games = Games;
        return new LibraryStatus(Revision, games.Count, s.Syncing, s.LastSyncedAt, s.SyncError,
            games.Values.Count(g => g.UpdateAvailable && g.InstallStatus is InstallStatus.Installed or InstallStatus.Broken));
    }

    // ------------------------------------------------------------------ cold start

    /// <summary>cache/library.json → the published map (revision 0). Synchronous and fast: no Legendary file is touched.</summary>
    public void LoadCache()
    {
        _art = LoadArtIndex();
        _c.Art.LoadPlaceholders();
        LibraryCacheFile? file = null;
        try
        {
            if (File.Exists(_cachePath))
            {
                file = JsonSerializer.Deserialize(File.ReadAllBytes(_cachePath), EngineJsonContext.Default.LibraryCacheFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _c.Log.Warn(Category, "cache/library.json is unreadable; starting from Legendary's files.", e);
        }

        if (file?.Games is not { Count: > 0 } games)
        {
            return;
        }

        var b = ImmutableDictionary.CreateBuilder<string, GameSummary>(StringComparer.Ordinal);
        foreach (var g in games)
        {
            if (g?.AppName is { Length: > 0 } && g.Title is not null && g.Art is not null)
            {
                // Runtime facts never survive a restart.
                b[g.AppName] = g with { RunState = RunState.Idle, ActiveJobId = null, ActiveJobKind = null };
            }
        }

        lock (_gate)
        {
            _published = b.ToImmutable();
        }

        _c.Log.Info(Category, $"library cache: {b.Count} games");
    }

    private ImmutableDictionary<string, GameArt> LoadArtIndex()
    {
        try
        {
            if (!File.Exists(_artIndexPath))
            {
                return ImmutableDictionary<string, GameArt>.Empty;
            }

            var b = ImmutableDictionary.CreateBuilder<string, GameArt>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_artIndexPath));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                string? Url(string k) => p.Value.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } u && CachedFileExists(u) ? u : null;
                var tall = Url("tall");
                var wide = Url("wide");
                if (tall is not null || wide is not null)
                {
                    b[p.Name] = new GameArt(tall ?? wide, tall ?? wide, wide ?? tall, Url("logo"), null);
                }
            }

            return b.ToImmutable();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ImmutableDictionary<string, GameArt>.Empty;
        }
    }

    private bool CachedFileExists(string url)
    {
        if (!url.StartsWith(WebHost.CachePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rel = Uri.UnescapeDataString(url[WebHost.CachePrefix.Length..]).Replace('/', Path.DirectorySeparatorChar);
        return !rel.Contains("..", StringComparison.Ordinal) && File.Exists(Path.Combine(_c.Paths.Cache, rel));
    }

    // ------------------------------------------------------------------ rebuild + deltas

    /// <summary>Something that feeds the summaries changed: rebuild and publish one coalesced delta shortly.</summary>
    public void Invalidate(bool rereadFiles = false)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingRebuild = true;
            _pendingReread |= rereadFiles;
            if (_flushArmed)
            {
                return;
            }

            _flushArmed = true;
        }

        var delay = _c.Options.LibraryCoalesceInterval;
        if (delay <= TimeSpan.Zero)
        {
            _ = Task.Run(() => FlushAsync());
        }
        else
        {
            _flushTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Runs any pending rebuild now and publishes its delta (if anything changed). Before Legendary's files were read once, a cached
    /// library is all there is and a flush leaves the rebuild to the pre-warm's re-read: nothing that asks for a flush (library.get,
    /// game.getDetail) may end up parsing 500 metadata files, or waiting behind the pre-warm that does.
    /// </summary>
    public async Task FlushAsync()
    {
        lock (_gate)
        {
            if (!_filesRead && !_pendingReread && _published.Count > 0 && !_disposed)
            {
                _flushArmed = false; // the rebuild stays pending (Invalidate set it) until the first read
                return;
            }
        }

        await _rebuild.WaitAsync().ConfigureAwait(false);
        try
        {
            bool reread;
            lock (_gate)
            {
                _flushArmed = false;
                if (!_pendingRebuild || _disposed)
                {
                    return;
                }

                _pendingRebuild = false;
                reread = _pendingReread;
                _pendingReread = false;
            }

            await RebuildCoreAsync(reread).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _c.Log.Error(Category, "library rebuild failed", e);
        }
        finally
        {
            _rebuild.Release();
        }
    }

    /// <summary>Re-reads Legendary's files and rebuilds now (startup, after a sync, after a job).</summary>
    public async Task ReloadAsync()
    {
        lock (_gate)
        {
            _pendingRebuild = true;
            _pendingReread = true;
        }

        await FlushAsync().ConfigureAwait(false);
    }

    private async Task RebuildCoreAsync(bool reread)
    {
        LegendaryLibrary lib;
        lock (_gate)
        {
            if (!reread && !_filesRead && _published.Count > 0)
            {
                // Cold start from the cache: an invalidation (startup publishes state) must not be the thing that reads
                // Legendary's files. Pre-warm does the first read (a reread).
                _pendingRebuild = true;
                return;
            }
        }

        if (reread || !FilesRead)
        {
            lib = await _reader.ReadAsync(Files, _c.Shutdown).ConfigureAwait(false);
            var unknown = lib.Items.IsEmpty && lib.Installed is { IsEmpty: true } && !Directory.Exists(_reader.MetadataDir);
            lock (_gate)
            {
                if (unknown && _published.Count > 0 && !_filesRead)
                {
                    // Legendary has no library on disk yet (or it was moved away): keep showing the cache until a sync.
                    _c.Log.Info(Category, "Legendary has no metadata yet; keeping the cached library.");
                    return;
                }

                _lib = lib;
                _filesRead = true;
            }

            _filesReadSignal.TrySetResult();

            UpdateBroken(lib);
        }

        lock (_gate)
        {
            lib = _lib;
        }

        var map = GameCatalog.Build(lib, Context(), _memo); // only games whose inputs changed are recomputed
        LibraryDelta? delta = null;
        var updatesChanged = false;
        lock (_gate)
        {
            var (upserts, removes) = GameCatalog.Diff(_published, map);
            if (upserts.Count > 0 || removes.Count > 0)
            {
                updatesChanged = upserts.Any(u => !_published.TryGetValue(u.AppName, out var old) || old.UpdateAvailable != u.UpdateAvailable || old.LatestVersion != u.LatestVersion);
                _revision++;
                _published = map;
                delta = new LibraryDelta(_revision, false, upserts, removes);
            }
        }

        if (delta is not null)
        {
            _c.Hub.Sink.Publish(RpcEvents.Library, delta);
            _c.Hub.Invalidate(); // LibraryStatus (revision, counts) lives in the snapshot
            ScheduleCacheSave();
            if (updatesChanged)
            {
                UpdatesChanged?.Invoke();
            }
        }

        // Every rebuild, not only one that published a delta: whether a known update can be measured depends on more than the
        // summaries (signed in yet? writer lane idle? the previous measurement finished?). The first rebuild of a fresh start can
        // publish the update before the pre-warm has loaded the account, and nothing would change afterwards to ask again.
        // MeasureUpdateSizes asks each (game, installed, latest) at most once, so this is cheap when there is nothing to do.
        MeasureUpdateSizes(map);
    }

    private CatalogContext Context()
    {
        var s = _c.Hub.State;
        var lib = Files;
        var runtime = ImmutableDictionary.CreateBuilder<string, GameRuntime>(StringComparer.Ordinal);
        GameRuntime Get(string app) => runtime.TryGetValue(app, out var r) ? r : GameRuntime.Idle;

        foreach (var (app, session) in s.Sessions)
        {
            runtime[app] = Get(app) with { RunState = session.State };
        }

        foreach (var job in _c.Jobs.Snapshot())
        {
            if (StateHub.IsActive(job) && Get(job.AppName).ActiveJobId is null)
            {
                runtime[job.AppName] = Get(job.AppName) with { ActiveJobId = job.Id, ActiveJobKind = job.Kind };
            }
        }

        foreach (var app in s.Broken)
        {
            runtime[app] = Get(app) with { Broken = true };
        }

        foreach (var (app, status) in _c.SaveStatuses)
        {
            runtime[app] = Get(app) with { SaveStatus = status };
        }

        var games = _c.Games.Current;
        foreach (var (app, m) in s.Sizes)
        {
            var view = GameCatalog.InstalledOf(app, lib, games.TryGetValue(app, out var e) ? e : GameEntry.Empty);
            if (view is not null && string.Equals(m.Path, view.InstallPath, StringComparison.OrdinalIgnoreCase) && m.Version == view.Version)
            {
                runtime[app] = Get(app) with { InstalledSizeBytes = m.Bytes };
            }
        }

        foreach (var (app, u) in s.UpdateSizes)
        {
            var view = GameCatalog.InstalledOf(app, lib, games.TryGetValue(app, out var e) ? e : GameEntry.Empty);
            if (view is not null && view.Version == u.Installed && GameCatalog.LatestVersion(app, lib.Items.GetValueOrDefault(app), lib) == u.Latest)
            {
                runtime[app] = Get(app) with { UpdateDownloadBytes = u.DownloadBytes };
            }
        }

        return new CatalogContext(games, runtime.ToImmutable(), _c.Settings.CloudSaves, _art, _c.Art.Placeholders);
    }

    /// <summary>Installed games whose folder no longer holds the game (profile probe, else the installed executable).</summary>
    private void UpdateBroken(LegendaryLibrary lib)
    {
        if (lib.Installed is not { } installed)
        {
            return;
        }

        var broken = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var (app, g) in installed)
        {
            if (g.IsDlc)
            {
                continue; // a DLC lives in its base game's folder and often has no executable of its own
            }

            if (!GameProfiles.LooksLikeInstall(app, g.InstallPath, g.Executable))
            {
                broken.Add(app);
            }
        }

        var set = broken.ToImmutable();
        _c.Hub.Mutate(s => s.Broken.SetEquals(set) ? s : s with { Broken = set });
    }

    // ------------------------------------------------------------------ library.get / detail

    /// <summary>
    /// The whole library at the current revision. It never waits for a parse of Legendary's files: at cold start it is the cached
    /// library (cache/library.json) and the file-based rebuild follows as a delta, which the UI applies in order. Only with no cache
    /// at all (first run) does it wait, bounded, for that first read. A rebuild already under way is awaited for a moment at most.
    /// </summary>
    public async Task<LibraryDelta> GetAsync()
    {
        bool cold;
        lock (_gate)
        {
            cold = !_filesRead && _published.Count > 0;
        }

        if (!cold)
        {
            try
            {
                await FlushAsync().WaitAsync(FilesRead ? FlushTimeBox : FirstReadTimeBox).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still rebuilding: answer with what is published; the delta follows.
            }
        }

        lock (_gate)
        {
            if (_sorted is null || _sortedRevision != _revision)
            {
                _sorted = [.. _published.Values.OrderBy(g => g.AppName, StringComparer.Ordinal)];
                _sortedRevision = _revision;
            }

            return new LibraryDelta(_revision, true, _sorted, []);
        }
    }

    /// <summary>The UI is looking at this game: measure its pending update's download size now (once; a no-op when known or busy).</summary>
    public void RequestUpdateSize(string app)
    {
        if (Summary(app) is { UpdateAvailable: true, DownloadSizeBytes: null })
        {
            MeasureUpdateSizes(Games, app);
        }
    }

    public GameDetail Detail(string app, SaveState? saves)
    {
        var summary = Summary(app) ?? throw new GliderException(Errors.NotFound(app));
        var item = CatalogItem(app);
        var entry = _c.Games.Get(app);
        var installed = Installed(app);
        var dlc = Games.Values.Where(g => g.BaseAppName == app).OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).Select(g => g.AppName).ToList();
        var antiCheat = GameProfiles.DetectAntiCheat(app, summary.InstallPath);
        var quirks = new List<string>();
        if (antiCheat == AntiCheat.EasyAntiCheat)
        {
            quirks.Add("Easy Anti-Cheat");
        }
        else if (antiCheat == AntiCheat.BattlEye)
        {
            quirks.Add("BattlEye");
        }

        if (summary.ThirdPartyName is { } store)
        {
            quirks.Add($"Launches through {store}");
        }

        if (GameProfiles.For(app).TrackedProcessNames.Count > 0)
        {
            quirks.Add("Launcher process tracked");
        }

        string? eglManifest = null;
        if (summary.SharedWithEgl && summary.InstallPath is { } path)
        {
            try
            {
                eglManifest = _c.Egl.FindByLocation(path, app)?.ItemPath;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new GameDetail(
            summary,
            item?.Description,
            null,
            item?.ReleaseDate,
            dlc,
            [],
            [],
            saves,
            entry.Settings,
            item?.LaunchOptions ?? [],
            entry.Shortcuts,
            quirks,
            antiCheat,
            installed?.Executable,
            entry.Install?.InstalledAt,
            eglManifest);
    }

    // ------------------------------------------------------------------ background sync / update check (metadata lane)

    /// <summary>`list -T --json` then a re-read; returns at once. One sync at a time.</summary>
    public Task RefreshInBackground(bool userInitiated) => StartSync(userInitiated, full: true);

    /// <summary>`list-installed --check-updates --json` then a re-read; returns at once.</summary>
    public Task CheckUpdatesInBackground(bool userInitiated) => StartSync(userInitiated, full: false);

    private Task StartSync(bool userInitiated, bool full)
    {
        lock (_gate)
        {
            if (!_syncTask.IsCompleted || _disposed)
            {
                return _syncTask;
            }

            _syncTask = Task.Run(() => SyncAsync(userInitiated, full, _c.Shutdown));
            return _syncTask;
        }
    }

    /// <summary>The sync itself (awaitable; pre-warm runs it inline). Never throws.</summary>
    public async Task<GliderError?> SyncAsync(bool userInitiated, bool full, CancellationToken ct)
    {
        if (_c.Hub.State.Auth.Status != AuthStatus.SignedIn)
        {
            await ReloadAsync().ConfigureAwait(false); // offline library = the cached files
            return null;
        }

        _c.Hub.Mutate(s => s with { Syncing = true });
        GliderError? error = null;
        try
        {
            var outcome = await _c.Metadata.RunAsync(token => full ? _c.Legendary.RefreshLibraryAsync(token) : _c.Legendary.CheckUpdatesAsync(token), ct).ConfigureAwait(false);
            error = outcome.Error;
        }
        catch (GliderException e)
        {
            error = e.Error;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _c.Log.Warn(Category, "library sync failed", e);
            error = Errors.Network(e.Message);
        }
        finally
        {
            var failed = error;
            _c.Hub.Mutate(s => s with
            {
                Syncing = false,
                SyncError = failed,
                LastSyncedAt = failed is null && full ? _c.Now : s.LastSyncedAt,
                LastUpdateCheck = failed is null ? _c.Now : s.LastUpdateCheck,
                Connectivity = failed?.Code is ErrorCode.Network or ErrorCode.Offline ? Connectivity.Offline
                    : failed is null ? Connectivity.Online : s.Connectivity,
                Auth = failed?.Code is ErrorCode.AuthExpired or ErrorCode.NotSignedIn ? new AuthState(AuthStatus.Expired, s.Auth.Account) : s.Auth,
            });
        }

        if (error is not null)
        {
            _c.Log.Warn(Category, $"library {(full ? "sync" : "update check")} failed: {error.Code} {error.Detail}");
            if (userInitiated && error.Code is not (ErrorCode.Network or ErrorCode.Offline))
            {
                _c.Notify(NoticeLevel.Warning, error.Title, error.Message, error.Fix, toast: false);
            }
        }

        await ReloadAsync().ConfigureAwait(false);
        return error;
    }

    /// <summary>
    /// Update download sizes, lazily: one dry run per installed game with a pending update (metadata lane), never while the
    /// writer lane runs (the dry run needs Legendary's install lock and would be refused).
    /// </summary>
    private void MeasureUpdateSizes(ImmutableDictionary<string, GameSummary> map, string? onlyApp = null)
    {
        if (_c.Hub.State.Auth.Status != AuthStatus.SignedIn || _c.Jobs.IsBusy)
        {
            return;
        }

        var todo = new List<(string App, string Installed, string Latest)>();
        lock (_gate)
        {
            if (!_sizesTask.IsCompleted)
            {
                _sizesRerun = true; // an update published mid-measurement must not wait for an unrelated rebuild
                return;
            }

            foreach (var g in map.Values)
            {
                if ((onlyApp is null || g.AppName == onlyApp) && g.UpdateAvailable && g.DownloadSizeBytes is null && g.InstalledVersion is { } i && g.LatestVersion is { } l && _sizeTried.Add((g.AppName, i, l)))
                {
                    todo.Add((g.AppName, i, l));
                }
            }

            if (todo.Count == 0)
            {
                return;
            }

            _sizesTask = Task.Run(async () =>
            {
                foreach (var (app, installed, latest) in todo)
                {
                    try
                    {
                        if (_c.Jobs.IsBusy)
                        {
                            lock (_gate)
                            {
                                _sizeTried.Remove((app, installed, latest)); // try again after the job
                            }

                            continue;
                        }

                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_c.Shutdown);
                        cts.CancelAfter(TimeSpan.FromMinutes(10));
                        var sizes = await _c.Metadata.RunAsync(t => _c.Legendary.GetUpdateSizesAsync(app, t), cts.Token).ConfigureAwait(false);
                        if (sizes is not null)
                        {
                            _c.Hub.Mutate(s => s with { UpdateSizes = s.UpdateSizes.SetItem(app, new UpdateSize(installed, latest, sizes.DownloadBytes)) });
                            Invalidate();
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException || !_c.Shutdown.IsCancellationRequested)
                    {
                        _c.Log.Warn("update", $"couldn't measure the update size of {app}", e);
                    }
                }
            });
            _sizesTask.ContinueWith(_ =>
            {
                bool rerun;
                lock (_gate)
                {
                    rerun = _sizesRerun;
                    _sizesRerun = false;
                }

                if (rerun)
                {
                    Invalidate(); // the rebuild calls MeasureUpdateSizes again, now that the task has completed
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    // ------------------------------------------------------------------ cache

    /// <summary>
    /// A summary changed: cache/library.json is stale. It is rewritten off the hot path after a quiet delay and never more often than
    /// <see cref="MinCacheWriteInterval"/> (the file is only a cold-start hint; the next start re-reads Legendary's files anyway), and
    /// at shutdown. Nothing is written when nothing changed since the last write.
    /// </summary>
    private void ScheduleCacheSave()
    {
        TimeSpan wait;
        lock (_gate)
        {
            _cacheDirty = true;
            if (_cacheArmed || _disposed)
            {
                return;
            }

            _cacheArmed = true;
            var delay = _c.Options.LibraryCacheDelay;
            wait = delay <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(delay.Ticks, (MinCacheWriteInterval - (_c.Now - _lastCacheWrite)).Ticks));
        }

        if (wait <= TimeSpan.Zero)
        {
            _ = Task.Run(SaveCacheAsync);
        }
        else
        {
            _cacheTimer.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes cache/library.json if a summary changed since the last write (awaits a write already under way).</summary>
    public async Task SaveCacheAsync()
    {
        await _cacheWrite.WaitAsync().ConfigureAwait(false);
        try
        {
            GameSummary[] games;
            lock (_gate)
            {
                _cacheArmed = false;
                if (!_cacheDirty)
                {
                    return;
                }

                _cacheDirty = false;
                games = [.. _published.Values.OrderBy(g => g.AppName, StringComparer.Ordinal)];
                _lastCacheWrite = _c.Now;
            }

            var file = new LibraryCacheFile { SavedAt = _c.Now, Games = [.. games] };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(file, EngineJsonContext.Default.LibraryCacheFile);
            await AtomicFile.WriteAllBytesAsync(_cachePath, bytes).ConfigureAwait(false);
            Interlocked.Increment(ref _cacheWrites);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            lock (_gate)
            {
                _cacheDirty = true; // try again with the next change
            }

            _c.Log.Warn(Category, "couldn't write cache/library.json", e);
        }
        finally
        {
            _cacheWrite.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _flushTimer.Dispose();
        _cacheTimer.Dispose();
    }
}
