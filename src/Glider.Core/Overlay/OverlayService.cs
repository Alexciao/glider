using Glider.Core.Contracts;
using Glider.Core.Jobs;
using Glider.Core.Services;

namespace Glider.Core.Overlay;

/// <summary>
/// The EOS overlay (L§7): one global, per-user component. State is read from files and the registry interface, never by spawning
/// Legendary: <c>overlay_install.json</c> (version, path), <c>overlay_version.json</c> (latest build, refreshed by Legendary's own login at
/// most every 24 h), HKCU through <see cref="IOverlayRegistry"/>, and Epic's own overlay folders under Program Files (x86).
/// Install and update are <see cref="JobKind.Overlay"/> writer-lane jobs (progress, pause-free, cancellable); switching on/off is in-process
/// HKCU only (Legendary's <c>disable</c> also writes HKLM and crashes without administrator rights); remove runs <c>eos-overlay remove</c> inline.
/// </summary>
internal sealed class OverlayService : IDisposable
{
    private const string Category = "overlay";
    private const string Title = "Epic overlay";

    private readonly ServiceContext _c;
    private readonly IOverlayRegistry _registry;
    private readonly OverlayEnvironment _environment;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _registryLock = new(1, 1);
    private OverlayState _state = NotInstalled;

    /// <summary>What the user had before the running install/update job (null = unknown, e.g. a job resumed after a restart).</summary>
    private (bool Managed, bool Enabled)? _before;
    private string? _notifiedVersion;
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    public OverlayService(ServiceContext context, IOverlayRegistry registry, OverlayEnvironment environment)
    {
        _c = context;
        _registry = registry;
        _environment = environment;
    }

    public void Dispose() => _registryLock.Dispose();

    public static OverlayState NotInstalled { get; } = new(false, false, null, null, false, null, false);

    /// <summary>The cached state plus the live Busy flag (cheap: it is read for every snapshot).</summary>
    public OverlayState State()
    {
        OverlayState s;
        lock (_gate)
        {
            s = _state;
        }

        return s with { Busy = _c.Hub.ActiveJobs(JobInfo.OverlayAppName).Count > 0 };
    }

    /// <summary>Re-reads the files and the registry; publishes a snapshot when something changed. Never throws.</summary>
    public OverlayState Refresh()
    {
        OverlayState next;
        try
        {
            next = Compute();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _c.Log.Warn(Category, "could not read the overlay state", e);
            return State();
        }

        bool changed;
        lock (_gate)
        {
            changed = _state != next;
            _state = next;
        }

        if (changed)
        {
            _c.Hub.Invalidate();
        }

        return State();
    }

    private OverlayState Compute()
    {
        var config = _c.Paths.LegendaryConfig;
        var managed = OverlayFiles.ReadInstall(config);
        var managedFolder = managed is not null && OverlayFiles.IsOverlayFolder(managed.InstallPath) ? managed : null;
        var latest = OverlayFiles.ReadLatest(config);

        var registered = Normalize(_registry.GetOverlayPath());
        var enabled = registered is not null && OverlayFiles.IsOverlayFolder(registered);
        var external = FindExternal(managedFolder?.InstallPath, registered);

        var installPath = enabled ? registered : managedFolder?.InstallPath;
        var update = managedFolder is not null && latest.Version is { } l && l != managedFolder.Version;
        var machineLayers = _registry.MachineLayers();
        var note = !enabled && machineLayers.Count > 0
            ? "Epic's own services keep their overlay registered for all users on this PC; switching it off here only affects your account."
            : null;

        return new OverlayState(
            managedFolder is not null,
            enabled,
            managedFolder?.Version is { Length: > 0 } v ? v : null,
            latest.Version,
            update,
            installPath,
            false,
            managedFolder is not null,
            external,
            note);
    }

    /// <summary>The first valid overlay that Epic's own software provides (Legendary's search order, then whatever HKCU points at); never Glider's copy.</summary>
    private string? FindExternal(string? managedPath, string? registeredPath)
    {
        foreach (var candidate in _environment.ExternalCandidates().Append(registeredPath))
        {
            var path = Normalize(candidate);
            if (path is not null && OverlayFiles.IsOverlayFolder(path) && (managedPath is null || !PathsEqual(path, managedPath)))
            {
                return path;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ RPC

    /// <summary>Queues Glider's copy of the overlay: a download when none is managed, otherwise Legendary's update (a no-op when current).</summary>
    public async Task<JobInfo> InstallAsync()
    {
        var snapshot = Refresh();
        lock (_gate)
        {
            _before = (snapshot.Managed, snapshot.Enabled);
        }

        return _c.Jobs.Enqueue(new JobRequest(
            JobInfo.OverlayAppName,
            JobKind.Overlay,
            snapshot.Managed ? null : _c.Paths.Overlay,
            null,
            snapshot.Version,
            snapshot.LatestVersion,
            Title));
    }

    public async Task<OverlayState> SetEnabledAsync(GameFlagRequest request)
    {
        if (!string.Equals(request.AppName, JobInfo.OverlayAppName, StringComparison.Ordinal))
        {
            throw new GliderException(Errors.InvalidRequest($"The overlay is switched with appName \"{JobInfo.OverlayAppName}\"."));
        }

        await _registryLock.WaitAsync(_c.Shutdown);
        try
        {
            var state = Refresh();
            if (!request.Value)
            {
                await Task.Run(_registry.Disable, _c.Shutdown);
                _c.Log.Info(Category, "overlay switched off (HKCU entries removed)");
                return Refresh();
            }

            if (_c.Hub.ActiveJobs(JobInfo.OverlayAppName).Count > 0)
            {
                throw new GliderException(Errors.Busy("the overlay download"));
            }

            var folder = state.Managed ? PathOfManaged() : state.ExternalPath;
            if (folder is null || !OverlayFiles.IsOverlayFolder(folder))
            {
                throw new GliderException(Errors.OverlayNotInstalled());
            }

            await Task.Run(() => SwitchOn(folder), _c.Shutdown);
            _c.Log.Info(Category, $"overlay switched on at {folder}");
            return Refresh();
        }
        finally
        {
            _registryLock.Release();
        }
    }

    public async Task<OverlayState> RemoveAsync(CancellationToken ct)
    {
        var state = Refresh();
        if (!state.Managed)
        {
            return state; // nothing of Glider's to remove; Epic's own overlay is not ours to delete
        }

        if (_c.Hub.ActiveJobs(JobInfo.OverlayAppName).Count > 0)
        {
            throw new GliderException(Errors.Busy("the overlay download"));
        }

        if (_c.Hub.State.AnySessionActive)
        {
            throw new GliderException(Errors.GameRunning());
        }

        await _registryLock.WaitAsync(ct);
        try
        {
            var managedPath = PathOfManaged();
            var registered = Normalize(_registry.GetOverlayPath());
            var pointedElsewhere = registered is not null && managedPath is not null && !PathsEqual(registered, managedPath) &&
                                   OverlayFiles.IsOverlayFolder(registered) ? registered : null;

            var outcome = await _c.Legendary.RemoveOverlayAsync(ct);
            if (!outcome.Success)
            {
                throw new GliderException(outcome.Error ?? Errors.OverlayFailed());
            }

            await Task.Run(_registry.Disable, ct); // Legendary clears HKCU before it trips over HKLM; this makes sure
            if (pointedElsewhere is not null)
            {
                // The overlay Epic's own software provides was on before; Legendary's remove switches it off with ours.
                await Task.Run(() => SwitchOn(pointedElsewhere), ct);
            }
        }
        finally
        {
            _registryLock.Release();
        }

        _c.Log.Info(Category, "managed overlay removed");
        return Refresh();
    }

    // ------------------------------------------------------------------ job end

    public async Task OnJobFinishedAsync(JobInfo job, bool succeeded, Action<string, JobOutcome> setOutcome, CancellationToken ct)
    {
        (bool Managed, bool Enabled)? before;
        lock (_gate)
        {
            before = _before;
            _before = null;
        }

        if (!succeeded)
        {
            Refresh();
            if (job.Error is { Code: not ErrorCode.Cancelled } e)
            {
                _c.Notify(NoticeLevel.Error, e.Code == ErrorCode.OverlayFailed ? e.Title : "Epic overlay", e.Message, e.Fix, jobId: job.Id);
            }

            return;
        }

        await _registryLock.WaitAsync(ct);
        try
        {
            // Legendary's `update` re-enables an overlay the user switched off (no registry value = "install"). Keep their choice.
            if (before is { Managed: true, Enabled: false } && Compute().Enabled)
            {
                await Task.Run(_registry.Disable, ct);
                _c.Log.Info(Category, "overlay update finished; kept it switched off as the user had it");
            }
        }
        finally
        {
            _registryLock.Release();
        }

        var state = Refresh();
        var wasManaged = before?.Managed ?? job.FromVersion is not null;
        var message = state.Version is { } v ? $"Version {v}." : null;
        var summary = wasManaged ? "Epic overlay is up to date" : "Epic overlay installed";
        setOutcome(job.Id, new JobOutcome(OutcomeKind.Success, summary));
        _c.Notify(NoticeLevel.Success, summary, message, toast: false, jobId: job.Id);
    }

    // ------------------------------------------------------------------ periodic update check (metadata lane)

    /// <summary>
    /// Keeps the state fresh and the update flag honest: re-reads every <see cref="GliderServiceOptions.OverlayCheckInterval"/>, and when a
    /// managed overlay's version cache is older than a day (Legendary's login refreshes it as a side effect) runs the metadata lane's
    /// `list-installed --check-updates`. Tells the user once per new version.
    /// </summary>
    public async Task RunPeriodicAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _c.Log.Warn(Category, "overlay update check failed", e);
            }

            await Task.Delay(_c.Options.OverlayCheckInterval, _c.Time, ct);
        }
    }

    public async Task CheckOnceAsync(CancellationToken ct)
    {
        // One check at a time: "is the cache stale?" and "spawn Legendary" must not interleave with another caller's (the periodic
        // loop starts the moment pre-warm ends), or both see the stale cache, both spawn, and the user may be told twice.
        await _checkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = Refresh();
            if (state.Managed && _c.Hub.State.Auth.Status == AuthStatus.SignedIn && _c.Hub.ActiveJobs(JobInfo.OverlayAppName).Count == 0)
            {
                var latest = OverlayFiles.ReadLatest(_c.Paths.LegendaryConfig);
                if (latest.CheckedAt is not { } at || _c.Now - at > _c.Options.OverlayVersionMaxAge)
                {
                    var outcome = await _c.Metadata.RunAsync(token => _c.Legendary.CheckUpdatesAsync(token), ct);
                    _c.Log.Info(Category, outcome.Success ? "overlay version cache refreshed" : $"overlay version check failed: {outcome.Error?.Code}");
                    state = Refresh();
                }
            }

            if (state is { Managed: true, UpdateAvailable: true, LatestVersion: { } latestVersion } && _notifiedVersion != latestVersion)
            {
                _notifiedVersion = latestVersion;
                _c.Notify(NoticeLevel.Info, "Epic overlay update available", $"Version {latestVersion} is ready. Update it from Settings.", toast: false);
            }
        }
        finally
        {
            _checkGate.Release();
        }
    }

    // ------------------------------------------------------------------ helpers

    private string? PathOfManaged() => OverlayFiles.ReadInstall(_c.Paths.LegendaryConfig)?.InstallPath is { } p ? Normalize(p) : null;

    /// <summary>Legendary's `enable`: replace entries that point elsewhere, then write HKCU (lfs/eos.py add_registry_entries).</summary>
    private void SwitchOn(string folder)
    {
        var current = Normalize(_registry.GetOverlayPath());
        if (current is not null && !PathsEqual(current, folder))
        {
            _registry.Disable();
        }

        _registry.Enable(folder);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
}
