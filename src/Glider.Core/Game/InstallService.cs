using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>Install planning, adoption, and the job-backed game operations (install/update/verify/move/uninstall).</summary>
internal sealed class InstallService
{
    /// <summary>Head-room on top of the install size: max(2 GiB, 3%) — updates stage files and the OS needs breathing room.</summary>
    internal static long SpaceMargin(long installBytes) => Math.Max(2L * 1024 * 1024 * 1024, installBytes * 3 / 100);

    private readonly ServiceContext _c;
    private readonly UpdateChecker _updates;

    public InstallService(ServiceContext context, UpdateChecker updates)
    {
        _c = context;
        _updates = updates;
    }

    private string FolderName => _c.LastGameInfo?.FolderName is { Length: > 0 } f ? f : FortniteLayout.AppName;

    // ------------------------------------------------------------------ planning

    /// <summary>
    /// Settings.DefaultInstallBase, else <c>&lt;fixed drive with most free space&gt;\Games</c>. Not
    /// %ProgramFiles%\Epic Games: Legendary runs unelevated and can't create folders there.
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

    public async Task<InstallPlan> PlanAsync(InstallPlanRequest request, CancellationToken ct)
    {
        var basePath = string.IsNullOrWhiteSpace(request.BasePath) ? DefaultBase() : request.BasePath.Trim();
        RequireFullPath(basePath, "basePath");
        basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));

        var info = await _updates.GetInfoAsync(ct);
        var gamePath = Path.Combine(basePath, FolderName);
        var free = _c.Options.DiskInfo.FreeBytes(basePath) ?? 0;
        var install = info?.InstallSizeBytes ?? _c.Hub.State.InstallSizeBytes ?? 0;
        var download = info?.DownloadSizeBytes ?? _c.Hub.State.DownloadSizeBytes ?? 0;
        return new InstallPlan(
            basePath,
            gamePath,
            info?.Version ?? _c.Hub.State.LatestVersion ?? "",
            download,
            install,
            free,
            free >= install + SpaceMargin(install),
            FortniteLayout.LooksLikeInstall(gamePath));
    }

    // ------------------------------------------------------------------ install

    public async Task<JobInfo> InstallAsync(InstallRequest request, CancellationToken ct)
    {
        RequireFullPath(request.BasePath, "basePath");
        if (_c.Records.Current is not null)
        {
            throw new GliderException(Errors.AlreadyInstalled());
        }

        if (_c.ActiveJob(JobKind.Install) is { } existing)
        {
            return existing;
        }

        RequireNoOtherJob();
        await _c.RequireSignedInAsync(ct);
        var plan = await PlanAsync(new InstallPlanRequest(request.BasePath), ct);
        if (!plan.HasEnoughSpace)
        {
            throw new GliderException(Errors.InsufficientSpace(plan.InstallSizeBytes + SpaceMargin(plan.InstallSizeBytes), plan.FreeBytes));
        }

        var job = _c.Jobs.Enqueue(new JobRequest(JobKind.Install, plan.BasePath, null, null, NullIfEmpty(plan.Version)));
        _c.Log.Info("game", $"install queued to {plan.GamePath} ({job.Id})");
        return job;
    }

    // ------------------------------------------------------------------ validate / adopt

    public FolderValidation Validate(ValidateFolderRequest request)
    {
        if (request.Path is null)
        {
            throw new GliderException(Errors.InvalidRequest("path is required"));
        }

        var r = FolderInspector.Inspect(request.Path, _c.Egl, FolderName);
        return new FolderValidation(r.Valid, r.GamePath, r.Version, r.Reason);
    }

    public async Task<AdoptResult> AdoptAsync(AdoptRequest request, CancellationToken ct)
    {
        var byId = !string.IsNullOrWhiteSpace(request.CandidateId);
        var byPath = !string.IsNullOrWhiteSpace(request.Path);
        if (byId == byPath)
        {
            throw new GliderException(Errors.InvalidRequest("Pass exactly one of candidateId or path"));
        }

        if (!Enum.IsDefined(request.Verify))
        {
            throw new GliderException(Errors.InvalidRequest("Unknown verify mode"));
        }

        if (_c.Records.Current is not null)
        {
            throw new GliderException(Errors.AlreadyInstalled());
        }

        var gamePath = byId ? await ResolveCandidateAsync(request.CandidateId!, ct) : ResolvePath(request.Path!);
        if (_c.Egl.IsBusyWith(gamePath))
        {
            throw new GliderException(Errors.EglBusy());
        }

        RequireNoOtherJob();
        await _c.RequireSignedInAsync(ct);

        var outcome = await _c.Legendary.ImportAsync(ServiceContext.App, gamePath, ct);
        if (!outcome.Success)
        {
            throw new GliderException(outcome.Error ?? Errors.LegendaryFailed("import failed"));
        }

        var installed = await _c.Legendary.GetInstalledAsync(ServiceContext.App, ct)
            ?? throw new GliderException(Errors.LegendaryFailed("import reported success but the game isn't registered"));
        var item = _c.Egl.FindByLocation(installed.InstallPath) ?? _c.Egl.FindByLocation(gamePath);
        var latest = (await _updates.GetInfoAsync(ct))?.Version ?? _c.Hub.State.LatestVersion;

        var verify = DecideVerify(request.Verify, item, latest);
        var rec = new GameRecord(
            installed.InstallPath,
            installed.Version,
            installed.InstallSizeBytes,
            _c.Now,
            AdoptedFromEgl: item is not null,
            EglInstallationGuid: item?.InstallationGuid,
            NeedsVerification: verify,
            PlaytimeSeconds: _c.Records.Current?.PlaytimeSeconds ?? 0,
            LastPlayedAt: _c.Records.Current?.LastPlayedAt);
        await _c.SaveRecordAsync(rec);
        _c.Sizes.Request(filesChanged: false);
        _c.Log.Info("game", $"adopted {rec.InstallPath} v{rec.Version} (egl={item is not null}, verify={verify}, mode={request.Verify})");

        JobInfo? job = null;
        if (verify)
        {
            job = _c.Jobs.Enqueue(new JobRequest(JobKind.Verify, null, null, rec.Version, rec.Version));
        }

        return new AdoptResult(_c.Hub.BuildGame(), job);
    }

    /// <summary>
    /// Auto: skip the (100+ GB SHA-1) verify only when EGL's manifest says the folder is complete and at the
    /// latest version; anything else gets a background verify. Background/Full always verify (Full = the
    /// verify job runs before anything else and launch refuses while it's queued).
    /// </summary>
    internal static bool DecideVerify(VerifyMode mode, EglInstall? item, string? latest) => mode switch
    {
        VerifyMode.Auto => !(item is { IsIncomplete: false, NeedsValidation: false }
                             && latest is not null
                             && string.Equals(item.Version, latest, StringComparison.Ordinal)),
        _ => true,
    };

    private async Task<string> ResolveCandidateAsync(string id, CancellationToken ct)
    {
        var candidates = _c.Hub.State.Egl?.Candidates ?? [];
        var c = candidates.FirstOrDefault(x => x.Id == id);
        if (c is null)
        {
            var egl = await _c.Egl.ScanAsync(ct);
            _c.Hub.Mutate(s => s with { Egl = egl });
            c = egl.Candidates.FirstOrDefault(x => x.Id == id);
        }

        if (c is null)
        {
            throw new GliderException(Errors.InvalidRequest($"Unknown install candidate '{id}'"));
        }

        if (c.IsStale || !c.FolderExists || !c.ExecutableFound)
        {
            throw new GliderException(Errors.InvalidFolder("The Epic Games Launcher remembers Fortnite there, but the game files are gone."));
        }

        return c.InstallPath;
    }

    private string ResolvePath(string path)
    {
        var r = FolderInspector.Inspect(path, _c.Egl, FolderName);
        return r.Valid ? r.GamePath! : throw new GliderException(Errors.InvalidFolder(r.Reason ?? "Not a Fortnite folder."));
    }

    // ------------------------------------------------------------------ jobs on an installed game

    public async Task<JobInfo> UpdateAsync(CancellationToken ct)
    {
        var rec = RequireInstalled();
        if (_c.ActiveJob(JobKind.Update) is { } existing)
        {
            return existing;
        }

        RequireNoOtherJob();
        RequireGameClosed();
        await _c.RequireSignedInAsync(ct);
        return _c.Jobs.Enqueue(new JobRequest(JobKind.Update, null, null, rec.Version, _c.Hub.State.LatestVersion));
    }

    public JobInfo Verify()
    {
        var rec = RequireInstalled();
        if (_c.ActiveJob(JobKind.Verify) is { } existing)
        {
            return existing;
        }

        RequireNoOtherJob();
        RequireGameClosed();
        return _c.Jobs.Enqueue(new JobRequest(JobKind.Verify, null, null, rec.Version, rec.Version));
    }

    public JobInfo Move(MoveRequest request)
    {
        RequireFullPath(request.DestinationBase, "destinationBase");
        var rec = RequireInstalled();
        var dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.DestinationBase));
        var currentParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(rec.InstallPath));
        if (string.Equals(dest, currentParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new GliderException(Errors.InvalidRequest("Fortnite is already in that folder"));
        }

        if (dest.StartsWith(Path.TrimEndingDirectorySeparator(rec.InstallPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new GliderException(Errors.InvalidFolder("Choose a folder outside the current Fortnite folder."));
        }

        RequireNoOtherJob();
        RequireGameClosed();
        var sameVolume = string.Equals(Path.GetPathRoot(dest), Path.GetPathRoot(rec.InstallPath), StringComparison.OrdinalIgnoreCase);
        if (!sameVolume && _c.Options.DiskInfo.FreeBytes(dest) is { } free && free < rec.InstallSizeBytes + SpaceMargin(rec.InstallSizeBytes))
        {
            throw new GliderException(Errors.InsufficientSpace(rec.InstallSizeBytes + SpaceMargin(rec.InstallSizeBytes), free));
        }

        return _c.Jobs.Enqueue(new JobRequest(JobKind.Move, dest, null, rec.Version, rec.Version));
    }

    public JobInfo Uninstall(UninstallRequest request)
    {
        if (!Enum.IsDefined(request.Mode))
        {
            throw new GliderException(Errors.InvalidRequest("Unknown uninstall mode"));
        }

        var rec = RequireInstalled();
        RequireNoOtherJob();
        RequireGameClosed();
        _c.Log.Info("game", $"uninstall queued ({request.Mode}, sharedWithEgl={rec.EglInstallationGuid is not null})");
        return _c.Jobs.Enqueue(new JobRequest(JobKind.Uninstall, null, request.Mode, rec.Version, null));
    }

    // ------------------------------------------------------------------ guards

    private GameRecord RequireInstalled() => _c.Records.Current ?? throw new GliderException(Errors.NotInstalled());

    private void RequireGameClosed()
    {
        if (_c.IsGameRunning())
        {
            throw new GliderException(Errors.GameRunning());
        }
    }

    private void RequireNoOtherJob()
    {
        if (_c.Hub.FirstActiveJob() is { } j)
        {
            throw new GliderException(Errors.Busy(JobNoun(j.Kind)));
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
