using System.Collections.Concurrent;
using Glider.Core.Contracts;
using Glider.Core.Egl;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// Install planning and the job-backed game operations, per AppName: install (+ each selected DLC as its own writer job,
/// the base with <c>--skip-dlcs</c>, L§3), update, verify→repair→verify, move, uninstall, import of EGL installs and located
/// folders. Rules are per game: a busy or running game refuses work on itself, never on other games.
/// </summary>
internal sealed class InstallService
{
    /// <summary>Head-room on top of the install size: max(2 GiB, 3%) — updates stage files and the OS needs breathing room.</summary>
    internal static long SpaceMargin(long installBytes) => Math.Max(2L * 1024 * 1024 * 1024, installBytes * 3 / 100);

    private static readonly TimeSpan InfoFreshFor = TimeSpan.FromMinutes(10);

    private readonly ServiceContext _c;
    private readonly ConcurrentDictionary<string, (LegendaryGameInfo Info, DateTimeOffset At)> _info = new(StringComparer.Ordinal);

    public InstallService(ServiceContext context) => _c = context;

    // ------------------------------------------------------------------ planning

    /// <summary>
    /// Settings.DefaultInstallBase, else <c>&lt;fixed drive with most free space&gt;\Games</c>. Not %ProgramFiles%\Epic Games:
    /// Legendary runs unelevated and can't create folders there.
    /// </summary>
    public string DefaultBase()
    {
        if (_c.Settings.DefaultInstallBase is { Length: > 0 } b && Path.IsPathFullyQualified(b))
        {
            return b;
        }

        var best = _c.Options.DiskInfo.FixedDrives().OrderByDescending(d => d.FreeBytes).FirstOrDefault();
        var root = best?.RootPath ?? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "C:\\";
        return Path.Combine(root, "Games");
    }

    /// <summary>`info &lt;app&gt; --json` on the metadata lane (cached 10 min); null when signed out, offline or failed.</summary>
    public async Task<LegendaryGameInfo?> GetInfoAsync(string app, CancellationToken ct)
    {
        if (_info.TryGetValue(app, out var hit) && _c.Now - hit.At < InfoFreshFor)
        {
            return hit.Info;
        }

        if (_c.Hub.State.Auth.Status != AuthStatus.SignedIn)
        {
            return _info.TryGetValue(app, out var stale) ? stale.Info : null;
        }

        try
        {
            var info = await _c.Metadata.RunAsync(t => _c.Legendary.GetGameInfoAsync(app, t), ct);
            _info[app] = (info, _c.Now);
            return info;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _c.Log.Warn("game", $"game info for {app} unavailable", e);
            return _info.TryGetValue(app, out var stale) ? stale.Info : null;
        }
    }

    private GameSummary RequireGame(string? app)
    {
        if (string.IsNullOrWhiteSpace(app))
        {
            throw new GliderException(Errors.InvalidRequest("appName is required"));
        }

        return _c.Library.Summary(app) ?? throw new GliderException(Errors.NotFound(app));
    }

    public async Task<InstallPlan> PlanAsync(InstallPlanRequest request, CancellationToken ct)
    {
        var game = RequireGame(request.AppName);
        var basePath = string.IsNullOrWhiteSpace(request.BasePath) ? DefaultBase() : request.BasePath.Trim();
        RequireFullPath(basePath, "basePath");
        basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));

        var info = game.InstallStatus == InstallStatus.NotInstallable ? null : await GetInfoAsync(game.AppName, ct);
        var folderName = info?.FolderName is { Length: > 0 } fn && fn.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? fn : _c.Library.FolderName(game.AppName);
        var gamePath = game.Kind == GameKind.Game ? Path.Combine(basePath, folderName) : _c.Library.Summary(game.BaseAppName ?? "")?.InstallPath ?? basePath;
        var free = _c.Options.DiskInfo.FreeBytes(basePath) ?? 0;
        var install = info?.InstallSizeBytes ?? game.InstallSizeBytes ?? 0;
        var download = info?.DownloadSizeBytes ?? 0;

        var dlc = _c.Library.Games.Values
            .Where(g => g.BaseAppName == game.AppName && g.Kind == GameKind.Dlc)
            .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var installable = g.InstallStatus != InstallStatus.NotInstallable;
                var selected = installable && (request.Dlc is null || request.Dlc.Contains(g.AppName, StringComparer.Ordinal));
                return new DlcOption(g.AppName, g.Title, null, g.InstallSizeBytes, installable, selected);
            })
            .ToList();

        return new InstallPlan(
            game.AppName,
            game.Title,
            basePath,
            gamePath,
            info?.Version ?? game.LatestVersion ?? "",
            download,
            install,
            free,
            free >= install + SpaceMargin(install),
            game.Kind == GameKind.Game && GameProfiles.LooksLikeInstall(game.AppName, gamePath) && Directory.Exists(gamePath),
            [],
            dlc,
            [],
            game.ThirdParty,
            game.ThirdPartyName);
    }

    // ------------------------------------------------------------------ install

    public async Task<JobInfo> InstallAsync(InstallRequest request, CancellationToken ct)
    {
        var game = RequireGame(request.AppName);
        if (game.InstallStatus is InstallStatus.Installed or InstallStatus.Broken)
        {
            throw new GliderException(Errors.AlreadyInstalled(game.Title, game.AppName));
        }

        if (game.InstallStatus == InstallStatus.NotInstallable)
        {
            throw new GliderException(game.ThirdPartyName is { } store
                ? Errors.ThirdPartyRequired(store, game.Title, game.AppName)
                : Errors.InvalidRequest($"{game.Title} has nothing to install through Epic."));
        }

        if (_c.ActiveJob(game.AppName, JobKind.Install) is { } existing)
        {
            return existing;
        }

        RequireNoOtherJobOn(game);
        if (game.Kind != GameKind.Game)
        {
            return await InstallDlcAsync(game, request.Priority, ct);
        }

        RequireFullPath(request.BasePath, "basePath");
        await _c.RequireSignedInAsync(ct);
        var plan = await PlanAsync(new InstallPlanRequest(game.AppName, request.BasePath, request.Components, request.Dlc), ct);
        if (!plan.HasEnoughSpace)
        {
            throw new GliderException(Errors.InsufficientSpace(plan.InstallSizeBytes + SpaceMargin(plan.InstallSizeBytes), plan.FreeBytes));
        }

        var job = _c.Jobs.Enqueue(new JobRequest(game.AppName, JobKind.Install, plan.BasePath, null, null, NullIfEmpty(plan.Version), game.Title,
            new JobOptions(Components: request.Components is { Count: > 0 } c ? c : null), request.Priority));
        _c.Log.Info("game", $"install of {game.AppName} queued to {plan.GamePath} ({job.Id})");

        // Each wanted DLC is its own writer job after the base game (Legendary's --with-dlcs recursion is fragile, L§3).
        foreach (var d in plan.Dlc.Where(d => d.Selected && d.Installable))
        {
            var title = _c.TitleOf(d.AppName);
            _c.Jobs.Enqueue(new JobRequest(d.AppName, JobKind.Install, null, null, null, null, title, new JobOptions(BaseAppName: game.AppName)));
        }

        return job;
    }

    private async Task<JobInfo> InstallDlcAsync(GameSummary dlc, bool priority, CancellationToken ct)
    {
        var baseGame = dlc.BaseAppName is { } b ? _c.Library.Summary(b) : null;
        if (baseGame is null || (baseGame.InstallStatus is not (InstallStatus.Installed or InstallStatus.Broken) && _c.ActiveJob(baseGame.AppName, JobKind.Install) is null))
        {
            throw new GliderException(Errors.NotInstalled(baseGame?.Title, baseGame?.AppName) with { Message = $"Install {baseGame?.Title ?? "the base game"} first." });
        }

        await _c.RequireSignedInAsync(ct);
        return _c.Jobs.Enqueue(new JobRequest(dlc.AppName, JobKind.Install, null, null, null, dlc.LatestVersion, dlc.Title,
            new JobOptions(BaseAppName: baseGame.AppName), priority));
    }

    /// <summary>DLC changes become install/uninstall jobs; component changes need manifest data (Phase 2).</summary>
    public async Task<JobInfo> ModifyAsync(ModifyRequest request, CancellationToken ct)
    {
        var game = RequireInstalled(request.AppName);
        if (request.Components is { Count: > 0 })
        {
            throw new GliderException(Errors.NotAvailableYet("changing installed components")); // Phase 2
        }

        var wanted = (request.Dlc ?? []).ToHashSet(StringComparer.Ordinal);
        JobInfo? first = null;
        foreach (var d in _c.Library.Games.Values.Where(g => g.BaseAppName == game.AppName && g.Kind == GameKind.Dlc))
        {
            var installed = d.InstallStatus is InstallStatus.Installed or InstallStatus.Broken;
            if (wanted.Contains(d.AppName) && !installed && d.InstallStatus != InstallStatus.NotInstallable)
            {
                await _c.RequireSignedInAsync(ct);
                first ??= _c.Jobs.Enqueue(new JobRequest(d.AppName, JobKind.Install, null, null, null, d.LatestVersion, d.Title, new JobOptions(BaseAppName: game.AppName)));
            }
            else if (!wanted.Contains(d.AppName) && installed)
            {
                var j = _c.Jobs.Enqueue(new JobRequest(d.AppName, JobKind.Uninstall, null, UninstallMode.DeleteFiles, d.InstalledVersion, null, d.Title, new JobOptions(BaseAppName: game.AppName)));
                first ??= j;
            }
        }

        return first ?? throw new GliderException(Errors.InvalidRequest("Nothing to change."));
    }

    // ------------------------------------------------------------------ validate / import

    public FolderValidation Validate(ValidateFolderRequest request)
    {
        if (request.Path is null)
        {
            throw new GliderException(Errors.InvalidRequest("path is required"));
        }

        var r = FolderInspector.Inspect(request.Path, _c.Egl);
        if (!r.Valid)
        {
            return new FolderValidation(false, null, null, null, null, r.Reason);
        }

        var summary = _c.Library.Summary(r.AppName!);
        if (summary is null && _c.Library.Files.Items.Count > 0)
        {
            return new FolderValidation(false, r.AppName, null, r.GamePath, r.Version, "This game isn't on your Epic account.");
        }

        return new FolderValidation(true, r.AppName, summary?.Title ?? r.AppName, r.GamePath, r.Version, null);
    }

    /// <summary>One Import job per selected install (zero re-download). Ids that can't be imported are skipped (and logged).</summary>
    public async Task<EglImportResult> ImportEglAsync(EglImportRequest request, Func<CancellationToken, Task<EglState>> rescan, CancellationToken ct)
    {
        if (request.Ids is null || request.Ids.Count == 0)
        {
            throw new GliderException(Errors.InvalidRequest("Select at least one install"));
        }

        if (!Enum.IsDefined(request.Verify))
        {
            throw new GliderException(Errors.InvalidRequest("Unknown verify mode"));
        }

        await _c.RequireSignedInAsync(ct);
        var state = _c.Hub.BuildSnapshot().Egl;
        if (request.Ids.Any(id => state.Installs.All(i => i.Id != id)))
        {
            state = await rescan(ct);
        }

        var jobs = new List<JobInfo>();
        foreach (var id in request.Ids.Distinct(StringComparer.Ordinal))
        {
            var install = state.Installs.FirstOrDefault(i => i.Id == id);
            if (install is null)
            {
                _c.Log.Warn("egl", $"import: unknown install id {id}");
                continue;
            }

            if (install.Status != EglInstallStatus.Importable)
            {
                _c.Log.Info("egl", $"import: skipping {install.AppName} at {install.InstallPath} ({install.Status}: {install.Reason})");
                continue;
            }

            jobs.Add(EnqueueImport(install.AppName, install.InstallPath, request.Verify));
        }

        return new EglImportResult(jobs);
    }

    public async Task<JobInfo> ImportFolderAsync(LocateRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Verify))
        {
            throw new GliderException(Errors.InvalidRequest("Unknown verify mode"));
        }

        var v = Validate(new ValidateFolderRequest(request.Path));
        if (!v.Valid || v.AppName is null || v.GamePath is null)
        {
            throw new GliderException(Errors.InvalidFolder(v.Reason ?? "No game was found here."));
        }

        await _c.RequireSignedInAsync(ct);
        return EnqueueImport(v.AppName, v.GamePath, request.Verify);
    }

    private JobInfo EnqueueImport(string app, string gamePath, VerifyMode verify)
    {
        var summary = _c.Library.Summary(app);
        if (summary is { InstallStatus: InstallStatus.Installed } && summary.InstallPath is { } p && EglPath.Same(p, gamePath))
        {
            throw new GliderException(Errors.AlreadyInstalled(summary.Title, app));
        }

        if (_c.Egl.IsBusyWith(gamePath))
        {
            throw new GliderException(Errors.EglBusy());
        }

        if (_c.ActiveJob(app, JobKind.Import) is { } existing)
        {
            return existing;
        }

        if (summary is not null)
        {
            RequireNoOtherJobOn(summary);
        }

        return _c.Jobs.Enqueue(new JobRequest(app, JobKind.Import, gamePath, null, null, null, summary?.Title ?? app, new JobOptions(Verify: verify)));
    }

    /// <summary>
    /// Auto: skip the (possibly 100+ GB SHA-1) verify only when EGL's manifest says the folder is complete and at the latest
    /// version; anything else gets a verify. Background/Full always verify.
    /// </summary>
    internal static bool DecideVerify(VerifyMode mode, EglItem? item, string? latest) => mode switch
    {
        VerifyMode.Auto => !(item is { IsIncomplete: false, NeedsValidation: false }
                             && latest is not null
                             && string.Equals(item.Version, latest, StringComparison.Ordinal)),
        _ => true,
    };

    // ------------------------------------------------------------------ jobs on an installed game

    public async Task<JobInfo> UpdateAsync(string app, CancellationToken ct)
    {
        var game = RequireInstalled(app);
        if (_c.ActiveJob(app, JobKind.Update) is { } existing)
        {
            return existing;
        }

        RequireNoOtherJobOn(game);
        RequireGameClosed(game);
        await _c.RequireSignedInAsync(ct);
        return _c.Jobs.Enqueue(new JobRequest(app, JobKind.Update, null, null, game.InstalledVersion, game.LatestVersion, game.Title,
            game.BaseAppName is { } b ? new JobOptions(BaseAppName: b) : null));
    }

    public JobInfo Verify(string app)
    {
        var game = RequireInstalled(app);
        if (_c.ActiveJob(app, JobKind.Verify) is { } existing)
        {
            return existing;
        }

        RequireNoOtherJobOn(game);
        RequireGameClosed(game);
        return EnqueueVerify(game);
    }

    public JobInfo EnqueueVerify(GameSummary game) =>
        _c.Jobs.Enqueue(new JobRequest(game.AppName, JobKind.Verify, null, null, game.InstalledVersion, game.InstalledVersion, game.Title,
            game.BaseAppName is { } b ? new JobOptions(BaseAppName: b) : null));

    public JobInfo Move(MoveRequest request)
    {
        RequireFullPath(request.DestinationBase, "destinationBase");
        var game = RequireInstalled(request.AppName);
        if (game.Kind != GameKind.Game)
        {
            throw new GliderException(Errors.InvalidRequest("DLC move with their base game."));
        }

        var installPath = game.InstallPath!;
        var dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.DestinationBase));
        var currentParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installPath));
        if (string.Equals(dest, currentParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new GliderException(Errors.InvalidRequest($"{game.Title} is already in that folder"));
        }

        if (dest.StartsWith(Path.TrimEndingDirectorySeparator(installPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new GliderException(Errors.InvalidFolder($"Choose a folder outside the current {game.Title} folder."));
        }

        RequireNoOtherJobOn(game);
        RequireGameClosed(game);
        var size = game.InstalledSizeBytes ?? game.InstallSizeBytes ?? 0;
        var sameVolume = string.Equals(Path.GetPathRoot(dest), Path.GetPathRoot(installPath), StringComparison.OrdinalIgnoreCase);
        if (!sameVolume && _c.Options.DiskInfo.FreeBytes(dest) is { } free && free < size + SpaceMargin(size))
        {
            throw new GliderException(Errors.InsufficientSpace(size + SpaceMargin(size), free));
        }

        return _c.Jobs.Enqueue(new JobRequest(game.AppName, JobKind.Move, dest, null, game.InstalledVersion, game.InstalledVersion, game.Title));
    }

    public JobInfo Uninstall(UninstallRequest request)
    {
        if (!Enum.IsDefined(request.Mode))
        {
            throw new GliderException(Errors.InvalidRequest("Unknown uninstall mode"));
        }

        var game = RequireInstalled(request.AppName);
        if (_c.ActiveJob(game.AppName, JobKind.Uninstall) is { } existing)
        {
            return existing;
        }

        RequireNoOtherJobOn(game);
        RequireGameClosed(game);
        _c.Log.Info("game", $"uninstall of {game.AppName} queued ({request.Mode}, sharedWithEgl={game.SharedWithEgl})");
        return _c.Jobs.Enqueue(new JobRequest(game.AppName, JobKind.Uninstall, null, request.Mode, game.InstalledVersion, null, game.Title,
            game.BaseAppName is { } b ? new JobOptions(BaseAppName: b) : null));
    }

    // ------------------------------------------------------------------ guards

    private GameSummary RequireInstalled(string? app)
    {
        var game = RequireGame(app);
        return game.InstallStatus is InstallStatus.Installed or InstallStatus.Broken && game.InstallPath is not null
            ? game
            : throw new GliderException(Errors.NotInstalled(game.Title, game.AppName));
    }

    private void RequireGameClosed(GameSummary game)
    {
        if (_c.IsGameRunning(game.AppName))
        {
            throw new GliderException(Errors.GameRunning(game.Title, game.AppName));
        }
    }

    /// <summary>Another unfinished job on this game (any kind) conflicts; other games' jobs never do.</summary>
    private void RequireNoOtherJobOn(GameSummary game)
    {
        if (_c.Hub.ActiveJobs(game.AppName) is [var j, ..])
        {
            throw new GliderException(Errors.GameBusy(JobNoun(j.Kind), game.Title, game.AppName));
        }
    }

    internal static string JobNoun(JobKind k) => k switch
    {
        JobKind.Install => "the download",
        JobKind.Update => "the update",
        JobKind.Repair => "the repair",
        JobKind.Verify => "the verification",
        JobKind.Move => "the move",
        JobKind.Uninstall => "the uninstall",
        JobKind.Import => "the import",
        _ => "the current task",
    };

    internal static void RequireFullPath(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new GliderException(Errors.InvalidRequest($"{name} must be a full path"));
        }
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
