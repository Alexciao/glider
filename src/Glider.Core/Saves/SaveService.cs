using System.Collections.Concurrent;
using System.Collections.Immutable;
using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Legendary;
using Glider.Core.Library;
using Glider.Core.Services;

namespace Glider.Core.Saves;

internal sealed class SaveOptions
{
    /// <summary>The longest Play waits for the pre-launch sync; after that the game starts with the save on this PC.</summary>
    public TimeSpan PreLaunchTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Waits between the attempts to sync after a game closed (the first attempt is immediate). A failure never deletes anything.</summary>
    public IReadOnlyList<TimeSpan> PostExitRetryDelays { get; init; } = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    public SaveBackupOptions Backups { get; init; } = new();
}

/// <summary>Result of the sync that runs before a launch: a conflict stops the launch, everything else lets the game start.</summary>
internal sealed record PreLaunchResult(SaveConflict? Conflict, string? Message)
{
    public static PreLaunchResult Proceed { get; } = new(null, null);
}

/// <summary>
/// Cloud saves, the S lane of docs/v0.2-design.md §3. One `sync-saves` at a time, per game exclusive with writer jobs and with
/// the game running. Legendary decides by comparing two clocks and can overwrite a newer save; Glider decides by a 3-way
/// check against the ledger (<see cref="SaveSyncPlanner"/>), backs the local save up before anything is overwritten, runs
/// exactly one explicit direction, and verifies the result on disk (Legendary never checks its own uploads or downloads).
/// </summary>
internal sealed class SaveService : IDisposable
{
    private readonly ServiceContext _c;
    private readonly ILegendarySaves? _cli;
    private readonly SaveOptions _options;
    private readonly SaveLedger _ledger;
    private readonly SaveBackups _backups;
    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly ConcurrentDictionary<string, AppInfo> _info = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _busy = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _postExit = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _syncing = new(StringComparer.Ordinal);
    private int _setupBusy;

    public SaveService(ServiceContext context, ILegendarySaves? cli = null, SaveOptions? options = null)
    {
        _c = context;
        _options = options ?? context.Options.SaveOptions ?? new SaveOptions();
        _cli = cli ?? context.Options.SavesLegendary ?? (context.Legendary is LegendaryCli real ? new LegendarySavesCli(real.Runner, context.Log) : null);
        _ledger = new SaveLedger(context.Paths, context.Log);
        _backups = new SaveBackups(Path.Combine(context.Paths.Backups, "saves"), context.Log, context.Time, _options.Backups);
    }

    /// <summary>Starts a game after "Keep … and play" (set by GliderService).</summary>
    public Func<LaunchRequest, CancellationToken, Task<LaunchResult>>? Launcher { get; set; }

    internal SaveLedger Ledger => _ledger;

    internal SaveBackups Backups => _backups;

    /// <summary>Completes when every post-exit sync has finished (tests).</summary>
    internal Task PostExitIdle => Task.WhenAll(_postExit.Values);

    private sealed record AppInfo(
        SaveSyncStatus Status,
        string? Note = null,
        GliderError? LastError = null,
        SaveConflict? Conflict = null,
        DateTimeOffset? LocalModifiedAt = null,
        DateTimeOffset? CloudModifiedAt = null,
        string? SavePath = null);

    private enum Trigger
    {
        Manual,
        PreLaunch,
        PostExit,
        Resolve,
    }

    private sealed record Outcome(SaveSyncStatus Status, SaveConflict? Conflict = null, GliderError? Error = null, bool Skipped = false, string? Note = null)
    {
        /// <summary>Worth trying again later (offline, busy, Epic unreachable).</summary>
        public bool Retryable { get; init; }
    }

    private sealed record Ctx(string App, string Title, string InstallPath, string Template, SaveFilters Filters);

    // ------------------------------------------------------------------ state

    public bool IsEnabled(string app)
    {
        var summary = _c.Library.Summary(app);
        return summary is { CloudSaves: true } && _c.Settings.CloudSaves && _c.Games.Get(app).Settings.CloudSaves;
    }

    public SaveState Get(string app)
    {
        var summary = _c.Library.Summary(app) ?? throw new GliderException(Errors.NotFound(app));
        var entry = _c.Games.Get(app);
        var supported = summary.CloudSaves;
        var enabled = supported && _c.Settings.CloudSaves && entry.Settings.CloudSaves;
        var ledger = _ledger.Peek(app);
        _info.TryGetValue(app, out var info);
        var status = !supported ? SaveSyncStatus.NotSupported
            : !enabled ? SaveSyncStatus.Disabled
            : _syncing.ContainsKey(app) ? SaveSyncStatus.Syncing
            : info?.Status ?? SaveSyncStatus.Unknown;
        var note = !enabled ? null : info?.Note;
        return new SaveState(
            supported,
            enabled,
            status,
            ledger?.SavePath ?? info?.SavePath,
            info?.LocalModifiedAt,
            info?.CloudModifiedAt,
            ledger?.LastSyncedAt,
            enabled ? info?.LastError : null,
            supported ? _backups.Count(app) : 0,
            enabled ? info?.Conflict : null,
            note);
    }

    /// <summary>Waits until no save operation touches this game (and none is setting a save folder up): the writer lane calls it before a job starts.</summary>
    public async Task WhenIdleAsync(string app, CancellationToken ct)
    {
        while (_busy.ContainsKey(app) || Volatile.Read(ref _setupBusy) > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }
    }

    private void Publish(string app, AppInfo info)
    {
        _info[app] = info;
        _c.SaveStatuses = _c.SaveStatuses.SetItem(app, _syncing.ContainsKey(app) ? SaveSyncStatus.Syncing : info.Status);
        _c.Library.Invalidate();
    }

    private void SetSyncing(string app, bool on)
    {
        if (on)
        {
            _syncing[app] = true;
        }
        else
        {
            _syncing.TryRemove(app, out _);
        }

        Publish(app, _info.TryGetValue(app, out var i) ? i : new AppInfo(SaveSyncStatus.Unknown));
    }

    private void Update(string app, Func<AppInfo, AppInfo> change) =>
        Publish(app, change(_info.TryGetValue(app, out var i) ? i : new AppInfo(SaveSyncStatus.Unknown)));

    // ------------------------------------------------------------------ RPCs

    public async Task<SaveState> SyncAsync(SaveSyncRequest request, CancellationToken ct = default)
    {
        var app = request.AppName;
        var summary = RequireSupported(app);
        if (!IsEnabled(app))
        {
            return Get(app);
        }

        var outcome = await SyncCoreAsync(app, Trigger.Manual, request.Direction, null, ct).ConfigureAwait(false);
        if (outcome.Error is { } e)
        {
            throw new GliderException(e);
        }

        _ = summary;
        return Get(app);
    }

    public async Task<LaunchResult?> ResolveAsync(SaveResolveRequest request, CancellationToken ct = default)
    {
        var app = request.AppName;
        var summary = RequireSupported(app);
        if (!IsEnabled(app))
        {
            throw new GliderException(Errors.InvalidRequest($"Cloud saves are switched off for {summary.Title}."));
        }

        var direction = request.Keep == SaveSide.Local ? SyncDirection.Upload : SyncDirection.Download;
        var outcome = await SyncCoreAsync(app, Trigger.Resolve, direction, request.Keep, ct).ConfigureAwait(false);
        if (outcome.Error is { } e)
        {
            throw new GliderException(e);
        }

        if (!request.ThenLaunch)
        {
            return null;
        }

        var launcher = Launcher ?? throw new GliderException(Errors.NotAvailableYet("launching after resolving saves"));
        return await launcher(new LaunchRequest(app, null, SkipSaveSync: true), ct).ConfigureAwait(false);
    }

    public Task OpenBackupsAsync(string app)
    {
        RequireSupported(app);
        var dir = _backups.DirectoryFor(app);
        Directory.CreateDirectory(dir);
        _c.Host.OpenPath(dir);
        return Task.CompletedTask;
    }

    private GameSummary RequireSupported(string app)
    {
        var summary = _c.Library.Summary(app) ?? throw new GliderException(Errors.NotFound(app));
        if (!summary.CloudSaves)
        {
            throw new GliderException(Errors.InvalidRequest($"{summary.Title} has no cloud saves."));
        }

        return summary;
    }

    // ------------------------------------------------------------------ launch integration

    /// <summary>Before a launch: download if the cloud is newer, upload what changed, and stop only on a real conflict. Never blocks offline play.</summary>
    public async Task<PreLaunchResult> PreLaunchAsync(string app, bool offline, CancellationToken ct)
    {
        if (!IsEnabled(app))
        {
            return PreLaunchResult.Proceed;
        }

        if (offline)
        {
            Update(app, i => i with { Note = "Offline: your saves will sync the next time you're online." });
            return PreLaunchResult.Proceed;
        }

        using var timeout = new CancellationTokenSource(_options.PreLaunchTimeout, _c.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        Outcome outcome;
        try
        {
            outcome = await SyncCoreAsync(app, Trigger.PreLaunch, SyncDirection.Auto, null, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _c.Log.Warn("saves", $"{app}: the pre-launch sync took longer than {_options.PreLaunchTimeout.TotalSeconds:0} s; starting with the save on this PC");
            Update(app, i => i with { Note = "Syncing took too long; the game started with the save on this PC." });
            return new PreLaunchResult(null, "Cloud sync took too long; started with the save on this PC.");
        }

        if (outcome.Conflict is { } conflict)
        {
            return new PreLaunchResult(conflict, "Your cloud and local saves both changed. Choose which to keep.");
        }

        if (outcome.Error is { } error)
        {
            _c.Notify(NoticeLevel.Warning, $"Couldn't sync {_c.TitleOf(app)}'s cloud saves", "The game started with the save on this PC. " + error.Message, FixAction.Retry, toast: false, appName: app);
            return new PreLaunchResult(null, error.Message);
        }

        return PreLaunchResult.Proceed;
    }

    /// <summary>After the game closed: upload what changed (or download what another PC uploaded). Retries; never deletes anything on failure.</summary>
    public void AfterExit(string app)
    {
        if (!IsEnabled(app))
        {
            return;
        }

        _postExit[app] = Task.Run(async () =>
        {
            try
            {
                await PostExitAsync(app).ConfigureAwait(false);
            }
            catch (OperationCanceledException e)
            {
                _c.Log.Info("saves", $"{app}: post-exit sync cancelled: {e.Message}");
            }
            catch (Exception e)
            {
                _c.Log.Error("saves", $"{app}: post-exit sync crashed", e);
            }
            finally
            {
                _postExit.TryRemove(app, out _);
            }
        });
    }

    private async Task PostExitAsync(string app)
    {
        var ct = _c.Shutdown;
        var delays = _options.PostExitRetryDelays;
        Outcome? last = null;
        for (var attempt = 0; attempt <= delays.Count; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(delays[attempt - 1], _c.Time, ct).ConfigureAwait(false);
                if (!IsEnabled(app))
                {
                    return;
                }
            }

            last = await SyncCoreAsync(app, Trigger.PostExit, SyncDirection.Auto, null, ct).ConfigureAwait(false);
            if (last.Conflict is not null)
            {
                _c.Notify(NoticeLevel.Warning, $"{_c.TitleOf(app)}'s saves differ", "The cloud and this PC both changed. Choose which one to keep.", FixAction.ResolveSaves, appName: app);
                return;
            }

            if (!last.Retryable)
            {
                return;
            }
        }

        if (last?.Error is { } error)
        {
            _c.Notify(NoticeLevel.Warning, $"Couldn't upload {_c.TitleOf(app)}'s saves", error.Message + " Your save on this PC is untouched; Glider will try again when you next play.", FixAction.Retry, appName: app);
        }
    }

    // ------------------------------------------------------------------ the sync

    private async Task<Outcome> SyncCoreAsync(string app, Trigger trigger, SyncDirection direction, SaveSide? keep, CancellationToken ct)
    {
        var cli = _cli;
        if (cli is null)
        {
            return Fail(app, SaveErrors.Unavailable(_c.TitleOf(app), app), trigger, retryable: false);
        }

        var summary = _c.Library.Summary(app)!;
        var title = summary.Title;
        var manual = trigger is Trigger.Manual or Trigger.Resolve;

        if (summary.InstallStatus is not (InstallStatus.Installed or InstallStatus.Broken) || summary.InstallPath is not { } installPath)
        {
            return manual ? Fail(app, Errors.NotInstalled(title, app), trigger, retryable: false) : Skip(app, "Not installed.", retryable: false);
        }

        var signedIn = _c.Hub.State.Auth.Status == AuthStatus.SignedIn;
        if (_c.Hub.State.Connectivity == Connectivity.Offline || !signedIn)
        {
            if (manual)
            {
                return Fail(app, signedIn || _c.Hub.State.Auth.Status != AuthStatus.SignedOut ? Errors.Network("Cloud saves need a connection to Epic.") : Errors.NotSignedIn(), trigger, retryable: false);
            }

            var note = signedIn ? "Offline: your saves will sync the next time you're online." : "Sign in to Epic to sync your saves.";
            await MarkLocalStatusAsync(app, note, ct).ConfigureAwait(false);
            return Skip(app, note, retryable: true);
        }

        var item = _c.Library.CatalogItem(app);
        if (item?.CloudSaveFolder is not { } template)
        {
            return Fail(app, Errors.InvalidRequest($"{title} has no cloud saves."), trigger, retryable: false);
        }

        var ctx = new Ctx(app, title, installPath, template, SaveFilters.FromMetadata(item.Attribute("CloudIncludeList"), item.Attribute("CloudExcludeList")));

        await _lane.WaitAsync(ct).ConfigureAwait(false);
        _busy.AddOrUpdate(app, 1, (_, n) => n + 1);
        try
        {
            return await SyncLockedAsync(ctx, cli, trigger, direction, keep, ct).ConfigureAwait(false);
        }
        finally
        {
            _busy.AddOrUpdate(app, 0, (_, n) => n - 1);
            _busy.TryRemove(new KeyValuePair<string, int>(app, 0));
            _lane.Release();
        }
    }

    private async Task<Outcome> SyncLockedAsync(Ctx ctx, ILegendarySaves cli, Trigger trigger, SyncDirection direction, SaveSide? keep, CancellationToken ct)
    {
        var app = ctx.App;
        var manual = trigger is Trigger.Manual or Trigger.Resolve;

        // Per-game exclusivity (S lane): not while the game runs, not while a writer job touches it.
        if (trigger is Trigger.Manual or Trigger.Resolve && _c.IsGameRunning(app))
        {
            return Fail(app, Errors.GameRunning(ctx.Title, app), trigger, retryable: false);
        }

        var job = _c.ActiveJob(app) ?? (_c.Library.Summary(app)?.BaseAppName is { } b ? _c.ActiveJob(b) : null);
        if (job is not null)
        {
            var busy = Errors.GameBusy(InstallService.JobNoun(job.Kind), ctx.Title, app);
            return manual ? Fail(app, busy, trigger, retryable: false) : Skip(app, "Waiting for the game's download to finish.", retryable: true);
        }

        Update(app, i => i with { LastError = null, Note = null });
        SetSyncing(app, true);

        try
        {
            // 1. the save folder (first time: Legendary stores it in installed.json, a writer-lane write)
            var path = await ResolvePathAsync(ctx, cli, trigger, ct).ConfigureAwait(false);
            if (path.Outcome is { } stop)
            {
                return stop;
            }

            var savePath = path.Path!;
            if (SavePathResolver.Refusal(savePath, ctx.InstallPath, !ctx.Filters.IsEmpty) is { } refusal)
            {
                return Fail(app, SaveErrors.Failed(ctx.Title, app, "Glider won't sync this folder: " + refusal, refusal), trigger, retryable: false);
            }

            // 2. what each side looks like now, and what they looked like at the last sync
            var ledger = await _ledger.GetAsync(app, ct).ConfigureAwait(false);
            if (ledger is not null && !string.Equals(Path.GetFullPath(ledger.SavePath), Path.GetFullPath(savePath), StringComparison.OrdinalIgnoreCase))
            {
                _c.Log.Info("saves", $"{app}: the save folder changed from {ledger.SavePath} to {savePath}; treating this as a first sync");
                ledger = null;
            }

            var local = await Task.Run(() => SaveFolder.Scan(savePath, ctx.Filters, ledger?.Files, ct: ct), ct).ConfigureAwait(false);
            CloudSaveSnapshot cloud;
            try
            {
                cloud = await _c.Metadata.RunAsync(t => cli.ListSavesAsync(app, t), ct).ConfigureAwait(false);
            }
            catch (GliderException e) when (e.Error.Code is ErrorCode.Network or ErrorCode.Offline or ErrorCode.CdnUnavailable or ErrorCode.Timeout or ErrorCode.AuthExpired or ErrorCode.NotSignedIn && !manual)
            {
                await MarkLocalStatusAsync(app, "Epic isn't reachable: your saves will sync the next time you're online.", ct).ConfigureAwait(false);
                return Skip(app, "Epic isn't reachable: your saves will sync the next time you're online.", retryable: true);
            }
            catch (GliderException e)
            {
                return Fail(app, e.Error.Code == ErrorCode.SaveSyncFailed ? e.Error : SaveErrors.Failed(ctx.Title, app, e.Error.Message, e.Error.Detail, e.Error.Fix), trigger, retryable: e.Error.Code is ErrorCode.Network or ErrorCode.Offline);
            }

            Update(app, i => i with { LocalModifiedAt = local.LatestMtime, CloudModifiedAt = cloud.Head, SavePath = savePath });

            // 3. decide
            var decision = Decide(direction, keep, local, cloud, ledger);
            _c.Log.Info("saves", $"{app}: {decision.Kind} — {decision.Reason}");

            return decision.Kind switch
            {
                SaveDecisionKind.NoSave => await Record(app, savePath, local, cloud, "baseline", SaveSyncStatus.InSync, "No saves yet.", persist: false).ConfigureAwait(false),
                SaveDecisionKind.InSync => await Record(app, savePath, local, cloud, "baseline", SaveSyncStatus.InSync, null, persist: true).ConfigureAwait(false),
                SaveDecisionKind.Conflict => ConflictOutcome(ctx, local, cloud, ledger),
                SaveDecisionKind.Download => await DownloadAsync(ctx, cli, savePath, local, cloud, keep is SaveSide.Cloud ? "resolve-cloud" : "download", trigger, ct).ConfigureAwait(false),
                _ => await UploadAsync(ctx, cli, savePath, local, cloud, keep is SaveSide.Local ? "resolve-local" : "upload", trigger, ct).ConfigureAwait(false),
            };
        }
        catch (Exception e) when (e is not (GliderException or OperationCanceledException))
        {
            _c.Log.Error("saves", $"{app}: sync failed unexpectedly", e);
            return Fail(app, SaveErrors.Failed(ctx.Title, app, "Something unexpected went wrong. " + e.Message, e.GetType().Name), trigger, retryable: true);
        }
        finally
        {
            SetSyncing(app, false);
        }
    }

    /// <summary>The user's explicit choice overrides the 3-way check; Auto uses it.</summary>
    private static SaveDecision Decide(SyncDirection direction, SaveSide? keep, LocalSaveSnapshot local, CloudSaveSnapshot cloud, SaveLedgerEntry? ledger)
    {
        switch (direction)
        {
            case SyncDirection.Upload when !local.HasFiles:
                return new(SaveDecisionKind.NoSave, "Nothing to upload: the local save folder is empty.");
            case SyncDirection.Upload:
                return new(SaveDecisionKind.Upload, keep is null ? "Upload chosen." : "Keep the local save chosen.");
            case SyncDirection.Download when !cloud.HasSaves:
                return new(SaveDecisionKind.NoSave, "Nothing to download: the cloud has no save.");
            case SyncDirection.Download:
                return new(SaveDecisionKind.Download, keep is null ? "Download chosen." : "Keep the cloud save chosen.");
            default:
                return SaveSyncPlanner.Decide(local, cloud, ledger);
        }
    }

    // ------------------------------------------------------------------ save folder

    private sealed record PathResult(string? Path, Outcome? Outcome);

    private async Task<PathResult> ResolvePathAsync(Ctx ctx, ILegendarySaves cli, Trigger trigger, CancellationToken ct)
    {
        var app = ctx.App;
        if (await StoredPathAsync(app, ct).ConfigureAwait(false) is { } stored)
        {
            return new(stored, null);
        }

        // Legendary writes installed.json to remember the folder: only when no writer job runs (it holds installed.json.lock
        // for the whole download), and Legendary's own lock refuses the call if one starts meanwhile.
        if (_c.Jobs.IsBusy)
        {
            var busy = Errors.Busy("setting up cloud saves");
            return new(null, trigger is Trigger.Manual or Trigger.Resolve
                ? Fail(app, busy, trigger, retryable: false)
                : Skip(app, "Cloud saves will be set up when the current download finishes.", retryable: true));
        }

        Interlocked.Increment(ref _setupBusy);
        SyncRun run;
        try
        {
            run = await cli.SyncAsync(app, SyncMode.SetupPath, ct).ConfigureAwait(false);
        }
        catch (GliderException e)
        {
            return new(null, Fail(app, SaveErrors.Failed(ctx.Title, app, e.Error.Message, e.Error.Detail, e.Error.Fix), trigger, retryable: e.Error.Code is ErrorCode.Network or ErrorCode.Offline));
        }
        finally
        {
            Interlocked.Decrement(ref _setupBusy);
        }

        if (await StoredPathAsync(app, ct).ConfigureAwait(false) is { } now)
        {
            var own = SavePathResolver.Resolve(ctx.Template, SavePathVariables.ForCurrentUser(ctx.InstallPath, _c.Hub.State.Auth.Account?.AccountId ?? ""));
            if (own.Path is { } expected && !string.Equals(Path.GetFullPath(expected), Path.GetFullPath(now), StringComparison.OrdinalIgnoreCase))
            {
                _c.Log.Info("saves", $"{app}: Legendary stored the save folder {now}; Glider's own resolution of '{ctx.Template}' gives {expected}");
            }

            return new(now, null);
        }

        var detail = run.Error?.Detail ?? string.Join(" | ", run.Lines.TakeLast(3));
        var message = run.PathUnresolved
            ? "Glider couldn't work out where this game keeps its saves."
            : "Legendary didn't store a save folder for this game.";
        return new(null, Fail(app, SaveErrors.Failed(ctx.Title, app, message, detail), trigger, retryable: !run.PathUnresolved));
    }

    private async Task<string?> StoredPathAsync(string app, CancellationToken ct)
    {
        using var doc = await LegendaryFiles.ReadJsonAsync(LegendaryFiles.InstalledJson(_c.Paths.LegendaryConfig), ct).ConfigureAwait(false);
        return doc is not null && doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty(app, out var g)
            ? LegendaryFiles.NullIfEmpty(LegendaryFiles.Str(g, "save_path"))
            : null;
    }

    // ------------------------------------------------------------------ actions

    private Outcome ConflictOutcome(Ctx ctx, LocalSaveSnapshot local, CloudSaveSnapshot cloud, SaveLedgerEntry? ledger)
    {
        var conflict = new SaveConflict(ctx.App, ctx.Title, local.LatestMtime, cloud.Head, local.TotalBytes, null, ledger?.LastSyncedAt);
        Update(ctx.App, i => i with { Status = SaveSyncStatus.Conflict, Conflict = conflict, LastError = null, Note = "Both saves changed since they last matched." });
        return new Outcome(SaveSyncStatus.Conflict, conflict);
    }

    private async Task<Outcome> Record(string app, string savePath, LocalSaveSnapshot local, CloudSaveSnapshot cloud, string action, SaveSyncStatus status, string? note, bool persist)
    {
        if (persist)
        {
            await _ledger.SetAsync(app, new SaveLedgerEntry(savePath, _c.Now, local.Files, cloud.Manifests, action)).ConfigureAwait(false);
        }

        Update(app, i => i with { Status = status, Note = note, LastError = null, Conflict = null });
        return new Outcome(status);
    }

    private async Task<Outcome> DownloadAsync(Ctx ctx, ILegendarySaves cli, string savePath, LocalSaveSnapshot local, CloudSaveSnapshot cloud, string action, Trigger trigger, CancellationToken ct)
    {
        var app = ctx.App;

        // 1. the local save is copied away first; if that fails nothing is overwritten.
        SaveBackup? backup;
        try
        {
            backup = _backups.Create(app, savePath, local, BackupReasons.Overwritten);
        }
        catch (SaveBackupException e)
        {
            return Fail(app, SaveErrors.Failed(ctx.Title, app, "The cloud save wasn't downloaded because your save on this PC couldn't be backed up first. " + e.Message, e.Message), trigger, retryable: false);
        }

        // 2. exactly one directional call. Legendary deletes the local files before it fetches anything.
        SyncRun? run = null;
        Exception? thrown = null;
        try
        {
            run = await cli.SyncAsync(app, SyncMode.Download, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or GliderException)
        {
            thrown = e;
        }

        // 3. prove it on disk: files exist and carry a manifest's time.
        var after = await Task.Run(() => SaveFolder.Scan(savePath, ctx.Filters, null, ct: CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        var landed = thrown is null && run is { Error: null, DownloadReported: true, DownloadProblems: false } && SaveSyncPlanner.DownloadLanded(after, cloud);
        if (!landed)
        {
            RestoreOriginal(ctx, savePath, local, after, backup);
            if (thrown is OperationCanceledException)
            {
                throw thrown;
            }

            var cause = (thrown as GliderException)?.Error ?? run?.Error;
            var detail = cause?.Detail ?? string.Join(" | ", run?.Lines.TakeLast(4) ?? []);
            return Fail(app, SaveErrors.Failed(ctx.Title, app, "The cloud save couldn't be downloaded; your previous save on this PC was put back." + (backup is null ? "" : " A copy is also in the backups folder."), detail, FixAction.Retry), trigger, retryable: true);
        }

        await _ledger.SetAsync(app, new SaveLedgerEntry(savePath, _c.Now, after.Files, cloud.Manifests, action), CancellationToken.None).ConfigureAwait(false);
        Update(app, i => i with { Status = SaveSyncStatus.InSync, Conflict = null, LastError = null, Note = null, LocalModifiedAt = after.LatestMtime, CloudModifiedAt = cloud.Head });
        return new Outcome(SaveSyncStatus.InSync);
    }

    /// <summary>A failed download has already deleted the old files: put the original back (from the backup) and remove anything half-written.</summary>
    private void RestoreOriginal(Ctx ctx, string savePath, LocalSaveSnapshot original, LocalSaveSnapshot after, SaveBackup? backup)
    {
        try
        {
            if (backup is not null)
            {
                if (SaveSyncPlanner.LocalChanged(original.Files, after.Files))
                {
                    _backups.Restore(backup, savePath, ctx.Filters);
                    _c.Log.Warn("saves", $"{ctx.App}: the download did not finish; restored the previous save from {backup.Directory}");
                }

                return;
            }

            // The folder was empty before: remove what the failed download wrote.
            foreach (var f in after.Files)
            {
                File.Delete(Path.Combine(savePath, f.Path));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _c.Log.Error("saves", $"{ctx.App}: couldn't restore the previous save automatically; it is in {backup?.Directory}", e);
            _c.Notify(NoticeLevel.Error, $"{ctx.Title}'s save needs a manual restore", $"A download failed and Glider couldn't put your previous save back. A copy is in {backup?.Directory}.", FixAction.OpenLogs, appName: ctx.App);
        }
    }

    private async Task<Outcome> UploadAsync(Ctx ctx, ILegendarySaves cli, string savePath, LocalSaveSnapshot local, CloudSaveSnapshot cloud, string action, Trigger trigger, CancellationToken ct)
    {
        var app = ctx.App;

        // What gets uploaded is kept. An upload never removes anything from the cloud (every save is its own manifest), so the
        // cloud's previous saves stay recoverable; Glider cannot download them to a backup first (see docs/cloud-saves.md).
        try
        {
            _backups.Create(app, savePath, local, BackupReasons.Uploaded);
        }
        catch (SaveBackupException e)
        {
            _c.Log.Warn("saves", $"{app}: no copy of the uploaded save was kept: {e.Message}");
        }

        var latest = SaveFolder.LatestMtimeUnfiltered(savePath, ct) ?? local.LatestMtime ?? _c.Now;
        SyncRun run;
        try
        {
            run = await cli.SyncAsync(app, SyncMode.Upload, ct).ConfigureAwait(false);
        }
        catch (GliderException e)
        {
            return Fail(app, SaveErrors.Failed(ctx.Title, app, e.Error.Message, e.Error.Detail, FixAction.Retry), trigger, retryable: true);
        }

        if (run.Error is { } runError)
        {
            return Fail(app, SaveErrors.Failed(ctx.Title, app, runError.Message, runError.Detail, FixAction.Retry), trigger, retryable: runError.Code is ErrorCode.Network or ErrorCode.Offline or ErrorCode.LegendaryLocked);
        }

        if (run.NothingToUpload)
        {
            return Fail(app, SaveErrors.Failed(ctx.Title, app, "Epic's list of save files matched nothing to upload.", "No files to upload"), trigger, retryable: false);
        }

        // Legendary never checks the result of its PUTs: list the cloud again and look for the new save.
        CloudSaveSnapshot after;
        try
        {
            after = await _c.Metadata.RunAsync(t => cli.ListSavesAsync(app, t), CancellationToken.None).ConfigureAwait(false);
        }
        catch (GliderException e)
        {
            return Fail(app, SaveErrors.Failed(ctx.Title, app, "The upload finished but Glider couldn't confirm it with Epic. " + e.Error.Message, e.Error.Detail, FixAction.Retry), trigger, retryable: true);
        }

        if (!run.UploadFinished || !SaveSyncPlanner.UploadLanded(cloud.Manifests, after.Manifests, latest))
        {
            return Fail(app, SaveErrors.Failed(ctx.Title, app, "Epic didn't confirm the upload. Your save on this PC is untouched.", string.Join(" | ", run.Lines.TakeLast(3)), FixAction.Retry), trigger, retryable: true);
        }

        await _ledger.SetAsync(app, new SaveLedgerEntry(savePath, _c.Now, local.Files, after.Manifests, action), CancellationToken.None).ConfigureAwait(false);
        Update(app, i => i with { Status = SaveSyncStatus.InSync, Conflict = null, LastError = null, Note = null, LocalModifiedAt = local.LatestMtime, CloudModifiedAt = after.Head });
        return new Outcome(SaveSyncStatus.InSync);
    }

    // ------------------------------------------------------------------ outcomes

    private Outcome Fail(string app, GliderError error, Trigger trigger, bool retryable)
    {
        _c.Log.Warn("saves", $"{app}: sync failed ({trigger}): {error.Code} {error.Detail ?? error.Message}");
        Update(app, i => i with { Status = SaveSyncStatus.Error, LastError = error, Note = null });
        return new Outcome(SaveSyncStatus.Error, Error: error) { Retryable = retryable };
    }

    private Outcome Skip(string app, string note, bool retryable)
    {
        _c.Log.Info("saves", $"{app}: sync skipped: {note}");
        Update(app, i => i with { Note = note, LastError = null });
        return new Outcome(_info[app].Status, Skipped: true, Note: note) { Retryable = retryable };
    }

    /// <summary>Offline or unreachable: a local-only look at the folder so the status still says "local has unsynced changes".</summary>
    private async Task MarkLocalStatusAsync(string app, string note, CancellationToken ct)
    {
        try
        {
            var ledger = await _ledger.GetAsync(app, ct).ConfigureAwait(false);
            var item = _c.Library.CatalogItem(app);
            if (ledger is not null && item is not null)
            {
                var filters = SaveFilters.FromMetadata(item.Attribute("CloudIncludeList"), item.Attribute("CloudExcludeList"));
                var local = await Task.Run(() => SaveFolder.Scan(ledger.SavePath, filters, ledger.Files, ct: ct), ct).ConfigureAwait(false);
                if (SaveSyncPlanner.LocalChanged(ledger.Files, local.Files))
                {
                    Update(app, i => i with { Status = SaveSyncStatus.LocalNewer, Note = note, LocalModifiedAt = local.LatestMtime });
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _c.Log.Debug("saves", $"{app}: local check failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        _lane.Dispose();
        _ledger.Dispose();
    }
}

/// <summary>User-facing copy for cloud-save failures.</summary>
internal static class SaveErrors
{
    public static GliderError Failed(string title, string app, string message, string? detail, FixAction? fix = FixAction.Retry) =>
        new(ErrorCode.SaveSyncFailed, "Couldn't sync cloud saves", $"{title}: {message}", fix, detail, app);

    public static GliderError Unavailable(string title, string app) =>
        new(ErrorCode.SaveSyncFailed, "Cloud saves aren't available", $"{title}: this build of Glider can't reach Legendary's cloud-save commands.", null, null, app);
}
