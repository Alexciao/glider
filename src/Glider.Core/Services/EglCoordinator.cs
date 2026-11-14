using System.Collections.Immutable;
using Glider.Core.Contracts;
using Glider.Core.Egl;
using Glider.Core.Game;

namespace Glider.Core.Services;

/// <summary>
/// Joins what EGL (and the drive quick scan, and Glider v0.1's record) knows with the library: per install its app, title,
/// art, drive, status (importable / imported / stale / busy / not owned) and drift; plus drift reconciliation for every
/// imported, EGL-shared game (EGL newer → re-import, Glider newer → write back, unreconcilable → verify, missing → unlink).
/// </summary>
internal sealed class EglCoordinator
{
    private readonly ServiceContext _c;
    private readonly List<string> _verifyAfterRestore = [];

    public EglCoordinator(ServiceContext context) => _c = context;

    public async Task<EglState> RescanAsync(CancellationToken ct)
    {
        _c.Hub.Mutate(s => s with { EglScanning = true });
        try
        {
            var scan = await _c.Egl.ScanAsync(ct);
            _c.Hub.Mutate(s => s with { Egl = scan, EglDrift = Drifts(scan) });
        }
        finally
        {
            _c.Hub.Mutate(s => s with { EglScanning = false });
        }

        return Build();
    }

    public async Task<EglState> QuickScanAsync(CancellationToken ct)
    {
        await RescanAsync(ct);
        _c.Hub.Mutate(s => s with { EglScanning = true });
        try
        {
            var roots = _c.Options.DiskInfo.FixedDrives().Select(d => d.RootPath).ToList();
            var found = await Task.Run(() => _c.Egl.QuickScan(roots, ct), ct);
            var known = _c.Hub.State.Egl?.Installs ?? [];
            var extras = found.Where(f => !known.Any(k => EglPath.Same(k.InstallPath, f.InstallPath))).ToImmutableList();
            _c.Hub.Mutate(s => s with { EglExtras = extras });
        }
        finally
        {
            _c.Hub.Mutate(s => s with { EglScanning = false });
        }

        return Build();
    }

    private ImmutableDictionary<string, EglDrift> Drifts(EglScanResult scan)
    {
        var b = ImmutableDictionary.CreateBuilder<string, EglDrift>(StringComparer.Ordinal);
        foreach (var (app, entry) in _c.Games.Current)
        {
            if (entry.Install is { EglInstallationGuid: not null } rec)
            {
                try
                {
                    b[app] = _c.Egl.CheckDrift(rec).Kind switch
                    {
                        DriftKind.InSync => EglDrift.None,
                        DriftKind.EglNewer => EglDrift.EglNewer,
                        DriftKind.GliderNewer => EglDrift.GliderNewer,
                        _ => EglDrift.Unknown,
                    };
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    b[app] = EglDrift.Unknown;
                }
            }
        }

        return b.ToImmutable();
    }

    /// <summary>The contract view (cheap: only cached scan results and the library; no file I/O except Directory.Exists).</summary>
    public EglState Build()
    {
        var s = _c.Hub.State;
        var scan = s.Egl ?? EglScanResult.Empty;
        var lib = _c.Library;
        var owned = lib.Games;
        var libraryKnown = lib.FilesRead && lib.Files.Items.Count > 0;
        var installs = new List<EglInstall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(EglFoundInstall f)
        {
            if (!seen.Add(EglPath.Normalize(f.InstallPath)))
            {
                return;
            }

            var summary = owned.GetValueOrDefault(f.AppName);
            var imported = summary is { InstallStatus: InstallStatus.Installed or InstallStatus.Broken } && summary.InstallPath is { } p && EglPath.Same(p, f.InstallPath);
            var (status, reason) = imported ? (EglInstallStatus.Imported, (string?)null)
                : f.Busy ? (EglInstallStatus.Busy, "Epic Games Launcher is working on it right now.")
                : !f.FolderExists ? (EglInstallStatus.Stale, "This folder doesn't exist. The drive may be disconnected, or the game was moved or deleted.")
                : !f.LooksInstalled ? (EglInstallStatus.Stale, "The game files are missing from this folder.")
                : libraryKnown && summary is null ? (EglInstallStatus.NotOwned, "This game isn't on your Epic account.")
                : summary is { InstallStatus: InstallStatus.Installed } ? (EglInstallStatus.Stale, $"{summary.Title} is already installed in another folder.")
                : (EglInstallStatus.Importable, null);
            installs.Add(new EglInstall(
                f.Id,
                f.Source,
                f.AppName,
                summary?.Title ?? f.DisplayName ?? f.AppName,
                f.InstallPath,
                Path.GetPathRoot(f.InstallPath) ?? "",
                f.Version,
                f.SizeBytes,
                status,
                imported ? s.EglDrift.GetValueOrDefault(f.AppName, EglDrift.None) : EglDrift.None,
                reason,
                summary?.Art.TallThumb ?? summary?.Art.Tall));
        }

        foreach (var f in scan.Installs)
        {
            Add(f);
        }

        foreach (var f in s.EglExtras)
        {
            Add(f);
        }

        // Glider v0.1 records Legendary no longer knows (import again).
        if (lib.Files.Installed is { } legendaryInstalled)
        {
            foreach (var (app, entry) in _c.Games.Current)
            {
                if (entry.Install is { } rec && !legendaryInstalled.ContainsKey(app))
                {
                    var exists = Directory.Exists(rec.InstallPath);
                    Add(new EglFoundInstall(EglPath.CandidateId(rec.InstallPath), CandidateSource.GliderV1, app, null, rec.InstallPath, rec.Version, null,
                        exists, exists && GameProfiles.LooksLikeInstall(app, rec.InstallPath), false, rec.EglInstallationGuid));
                }
            }
        }

        return new EglState(
            scan.LauncherInstalled,
            scan.LauncherRunning,
            s.EglScanning,
            installs.OrderBy(i => i.Status).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            scan.Installs.Where(i => i.Busy).Select(i => i.AppName).Distinct(StringComparer.Ordinal).ToList(),
            _c.Settings.EglSync,
            scan.At);
    }

    /// <summary>"Ready to uninstall EGL?": installs only EGL manages, EGL busy, games linked to EGL.</summary>
    public EglReadiness Readiness()
    {
        var state = Build();
        var items = new List<ReadinessItem>();
        foreach (var i in state.Installs)
        {
            switch (i.Status)
            {
                case EglInstallStatus.Importable:
                    items.Add(new ReadinessItem(i.AppName, i.Title, ReadinessSeverity.Blocker, "Installed by the Epic Games Launcher but not imported into Glider yet."));
                    break;
                case EglInstallStatus.Busy:
                    items.Add(new ReadinessItem(i.AppName, i.Title, ReadinessSeverity.Blocker, "The Epic Games Launcher is still working on it."));
                    break;
                case EglInstallStatus.Imported when _c.Library.Summary(i.AppName)?.SharedWithEgl == true:
                    items.Add(new ReadinessItem(i.AppName, i.Title, ReadinessSeverity.Ok, "Imported; uninstalling the Epic Games Launcher keeps the game files."));
                    break;
                case EglInstallStatus.Stale:
                    items.Add(new ReadinessItem(i.AppName, i.Title, ReadinessSeverity.Warning, i.Reason ?? "The files are gone."));
                    break;
            }
        }

        foreach (var g in _c.Library.Games.Values.Where(g => g.ThirdParty is ThirdPartyStore.Ea or ThirdPartyStore.Ubisoft))
        {
            items.Add(new ReadinessItem(g.AppName, g.Title, ReadinessSeverity.Warning, $"Needs {g.ThirdPartyName}; that launcher keeps working without the Epic Games Launcher."));
        }

        return new EglReadiness(items.All(i => i.Severity != ReadinessSeverity.Blocker), items);
    }

    // ------------------------------------------------------------------ drift (startup)

    /// <summary>For every imported EGL-shared game: reconcile with EGL's manifest. Never downloads, never reinstalls.</summary>
    public async Task ReconcileAllAsync(CancellationToken ct)
    {
        foreach (var (app, entry) in _c.Games.Current.ToList())
        {
            if (entry.Install is { EglInstallationGuid: not null } rec)
            {
                try
                {
                    await ReconcileAsync(rec, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _c.Log.Warn("egl", $"drift check of {app} failed", e);
                }
            }
        }
    }

    private async Task ReconcileAsync(InstallRecord rec, CancellationToken ct)
    {
        var app = rec.AppName;
        var drift = _c.Egl.CheckDrift(rec);
        _c.Log.Info("egl", $"{app} drift: {drift.Kind} (egl={drift.EglVersion}, glider={drift.GliderVersion})");
        switch (drift.Kind)
        {
            case DriftKind.EglNewer:
                if (_c.Hub.ActiveJobs(app).Count > 0 || _c.Jobs.IsBusy || _c.Egl.IsBusyWith(rec.InstallPath))
                {
                    return; // Try again next start; never race Legendary's lock or EGL.
                }

                // EGL updated/repaired the folder: re-import picks up its .egstore manifest. No download.
                var outcome = await _c.Legendary.ImportAsync(app, rec.InstallPath, ct);
                if (outcome.Success)
                {
                    await _c.SyncRecordAsync(app, ct, r => r with { EglInstallationGuid = rec.EglInstallationGuid });
                }
                else
                {
                    _c.Log.Warn("egl", $"re-import of {app} after an EGL update failed: {outcome.Error?.Code}");
                }

                break;

            case DriftKind.GliderNewer:
                if (_c.Settings.EglSync)
                {
                    var installed = await _c.Legendary.GetInstalledAsync(app, ct);
                    await _c.Egl.WriteBackAsync(_c.BuildWriteBack(rec, installed), ct);
                }

                break;

            case DriftKind.Unreconcilable:
                // A quick verify (never a reinstall), queued once persisted jobs are restored.
                lock (_verifyAfterRestore)
                {
                    _verifyAfterRestore.Add(app);
                }

                await _c.SaveRecordAsync(app, rec with { NeedsVerification = true });
                break;

            case DriftKind.EglMissing:
                // EGL forgot the install (uninstalled there, or moved): it's ours alone now.
                await _c.SaveRecordAsync(app, rec with { EglInstallationGuid = null });
                break;
        }
    }

    /// <summary>Apps whose drift asked for a verify (cleared).</summary>
    public IReadOnlyList<string> TakeVerifyRequests()
    {
        lock (_verifyAfterRestore)
        {
            var list = _verifyAfterRestore.ToList();
            _verifyAfterRestore.Clear();
            return list;
        }
    }
}

/// <summary>Library folders and fixed drives for the snapshot, refreshed at most every few seconds (drive queries aren't free).</summary>
internal sealed class StorageService
{
    private readonly ServiceContext _c;
    private readonly Lock _gate = new();
    private (StorageState State, long At, string Key)? _cached;

    public StorageService(ServiceContext context) => _c = context;

    public StorageState State()
    {
        var settings = _c.Settings;
        var key = string.Join('|', settings.LibraryFolders) + "#" + settings.DefaultInstallBase + "#" + _c.Library.Revision;
        var now = _c.Time.GetTimestamp();
        lock (_gate)
        {
            if (_cached is { } c && c.Key == key && _c.Time.GetElapsedTime(c.At, now) < _c.Options.StorageRefreshInterval)
            {
                return c.State;
            }
        }

        var drives = _c.Options.DiskInfo.FixedDrives();
        var installed = _c.Library.Games.Values.Where(g => g.InstallPath is not null && g.Kind == GameKind.Game).ToList();
        var folders = new List<LibraryFolder>();
        foreach (var path in settings.LibraryFolders)
        {
            var root = Path.GetPathRoot(path) ?? "";
            var drive = drives.FirstOrDefault(d => string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase));
            var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
            folders.Add(new LibraryFolder(
                path,
                string.Equals(path, settings.DefaultInstallBase, StringComparison.OrdinalIgnoreCase),
                Directory.Exists(path),
                root,
                drive?.FreeBytes ?? _c.Options.DiskInfo.FreeBytes(path) ?? 0,
                installed.Count(g => g.InstallPath!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))));
        }

        var state = new StorageState(folders, [.. drives.Select(d => new DriveInfoDto(d.RootPath, null, d.FreeBytes, d.TotalBytes, false))]);
        lock (_gate)
        {
            _cached = (state, now, key);
        }

        return state;
    }
}
