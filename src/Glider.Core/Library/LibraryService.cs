using System.Collections.Immutable;
using System.Text.Json;
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
internal sealed class LibraryService : IDisposable
{
    private const string Category = "library";

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
    private ImmutableDictionary<string, GameSummary> _published = ImmutableDictionary<string, GameSummary>.Empty;
    private ImmutableDictionary<string, GameArt> _art = ImmutableDictionary<string, GameArt>.Empty;
    private long _revision;
    private bool _pendingRebuild;
    private bool _pendingReread;
    private bool _flushArmed;
    private bool _disposed;
    private Task _syncTask = Task.CompletedTask;
    private Task _sizesTask = Task.CompletedTask;

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

    public LegendaryInstalledGame? Installed(string app) => Files.Installed?.GetValueOrDefault(app);

    /// <summary>Catalog FolderName (Legendary installs into &lt;base&gt;\&lt;FolderName&gt;), falling back to the app name.</summary>
    public string FolderName(string app) => CatalogItem(app)?.FolderName is { Length: > 0 } f && f.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? f : app;

    /// <summary>Completes when the current background sync / update check finished (tests).</summary>
    internal Task SyncTask => _syncTask;

    /// <summary>Completes when the current update-size measurements finished (tests).</summary>
    internal Task SizesTask => _sizesTask;

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
            _ = Task.Run(FlushAsync);
        }
        else
        {
            _flushTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs any pending rebuild now and publishes its delta (if anything changed).</summary>
    public async Task FlushAsync()
    {
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

            UpdateBroken(lib);
        }

        lock (_gate)
        {
            lib = _lib;
        }

        var map = GameCatalog.Build(lib, Context());
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

            MeasureUpdateSizes(map);
        }
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

        return new CatalogContext(games, runtime.ToImmutable(), _c.Settings.CloudSaves, _art);
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

    public async Task<LibraryDelta> GetAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        lock (_gate)
        {
            return new LibraryDelta(_revision, true, [.. _published.Values.OrderBy(g => g.AppName, StringComparer.Ordinal)], []);
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
    private void MeasureUpdateSizes(ImmutableDictionary<string, GameSummary> map)
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
                return;
            }

            foreach (var g in map.Values)
            {
                if (g.UpdateAvailable && g.DownloadSizeBytes is null && g.InstalledVersion is { } i && g.LatestVersion is { } l && _sizeTried.Add((g.AppName, i, l)))
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
        }
    }

    // ------------------------------------------------------------------ cache

    private void ScheduleCacheSave()
    {
        var delay = _c.Options.LibraryCacheDelay;
        if (delay <= TimeSpan.Zero)
        {
            _ = SaveCacheAsync();
        }
        else
        {
            _cacheTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    public async Task SaveCacheAsync()
    {
        try
        {
            var file = new LibraryCacheFile { SavedAt = _c.Now, Games = [.. Games.Values.OrderBy(g => g.AppName, StringComparer.Ordinal)] };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(file, EngineJsonContext.Default.LibraryCacheFile);
            await AtomicFile.WriteAllBytesAsync(_cachePath, bytes).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _c.Log.Warn(Category, "couldn't write cache/library.json", e);
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
