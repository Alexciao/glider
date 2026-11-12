using System.Text.Json;
using Glider.Core.Contracts;

namespace Glider.Core.Egl;

/// <summary>What <c>legendary import</c> will conclude for a folder, predicted from its <c>.egstore</c> (core.py:1890-1924).</summary>
public sealed record EglAdoptionInfo(
    /// <summary>A <c>.mancpn</c> for Fortnite exists in <c>.egstore</c>.</summary>
    bool HasEglMetadata,
    /// <summary>The matching <c>&lt;guid&gt;.manifest</c> exists, so import needs no network manifest.</summary>
    bool HasEglManifest,
    string? ManifestGuid,
    /// <summary><c>.egstore\bps</c> exists or <c>.egstore\Pending</c> is non-empty (an EGL operation was in progress).</summary>
    bool StagingPresent,
    /// <summary>Legendary will set needs_verification (=> the next update becomes a full repair pass).</summary>
    bool LegendaryWillRequireVerification,
    /// <summary>
    /// Glider should verify after import even if Legendary won't require it: Legendary 0.21.1 trusts a folder that
    /// has a .mancpn but no .manifest, records the <i>latest</i> version and skips verification (core.py:1909-1920).
    /// </summary>
    bool VerifyRecommended,
    string? EglVersion);

/// <summary>
/// EGL coexistence: detection of EGL-managed Fortnite installs, busy detection, drift checks and EGL manifest write-back
/// that mirrors Legendary 0.21.1's <c>egl_export</c>. Reads never modify any EGL file; every write is backed up and atomic.
/// See docs/egl.md.
/// </summary>
public sealed class EglService : IEglService, IDisposable
{
    internal const string LogCategory = "egl";

    internal const string InstallingReason = "Epic Games Launcher is installing Fortnite, so Glider will wait and continue automatically.";
    internal const string UpdatingReason = "Epic Games Launcher is updating Fortnite, so Glider will wait and continue automatically.";

    private readonly EglEnvironment _env;
    private readonly AppPaths _paths;
    private readonly ILog _log;
    private readonly IProcessProbe _processes;
    private readonly ISettingsStore? _settings;
    private readonly IGameRecordStore? _records;
    private readonly EglServiceOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EglService(
        EglEnvironment environment,
        AppPaths paths,
        ILog? log = null,
        IProcessProbe? processes = null,
        ISettingsStore? settings = null,
        IGameRecordStore? records = null,
        EglServiceOptions? options = null)
    {
        _env = environment;
        _paths = paths;
        _log = log ?? NullLog.Instance;
        _processes = processes ?? SystemProcessProbe.Instance;
        _settings = settings;
        _records = records;
        _options = options ?? new EglServiceOptions();
    }

    /// <summary>EGL's data dir (<c>…\Epic\EpicGamesLauncher\Data</c>): its presence means EGL is installed.</summary>
    internal string DataDir => Path.GetDirectoryName(_env.ManifestsDir)!;

    internal string PendingManifestsDir => Path.Combine(_env.ManifestsDir, "Pending");

    // =============================================== reading ===============================================

    internal sealed record ItemFile(string Path, Dictionary<string, object?> Raw, EglManifest Manifest, DateTime LastWriteUtc)
    {
        public string Guid => Manifest.InstallationGuid is { Length: > 0 } g ? g : System.IO.Path.GetFileNameWithoutExtension(Path);

        public EglInstall ToInstall() => new(
            Path, Guid, Manifest.InstallLocation, Manifest.AppVersionString ?? "",
            Manifest.InstallSize, Manifest.IsIncompleteInstall, Manifest.NeedsValidation);
    }

    /// <summary>
    /// Every parsable <c>*.item</c> in a directory (top level only). Invalid files are logged and skipped. With
    /// <paramref name="strict"/> (write paths), a file that can't be <i>read</i> (locked/denied) is retried and then fails
    /// the operation: skipping it could make Glider write a second .item for an install EGL already has.
    /// </summary>
    internal List<ItemFile> ReadItems(string dir, bool strict = false)
    {
        var list = new List<ItemFile>();
        if (!Directory.Exists(dir))
        {
            return list;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*.item", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(LogCategory, $"Can't list {dir}", ex);
            return list;
        }

        foreach (var f in files)
        {
            if (!f.EndsWith(".item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var bytes = strict ? ReadStrict(f) : ReadSmall(f);
                var raw = EglJson.TryParseObject(bytes, out var error);
                if (raw is null)
                {
                    _log.Warn(LogCategory, $"Skipping unreadable EGL manifest {f}: {error}");
                    continue;
                }

                var m = EglManifest.FromJson(raw);
                if (string.IsNullOrEmpty(m.AppName))
                {
                    _log.Warn(LogCategory, $"Skipping EGL manifest without AppName: {f}");
                    continue;
                }

                list.Add(new ItemFile(f, raw, m, File.GetLastWriteTimeUtc(f)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn(LogCategory, $"Skipping EGL manifest {f}", ex);
            }
        }

        return list;
    }

    /// <summary>Largest EGL metadata file (.item, .mancpn, LauncherInstalled.dat) Glider reads; real ones are a few KB.</summary>
    internal const long MaxMetadataBytes = 8L * 1024 * 1024;

    /// <summary>File.ReadAllBytes that refuses oversized files (ProgramData is writable by other software): IOException.</summary>
    internal static byte[] ReadSmall(string path)
    {
        var length = new FileInfo(path).Length; // FileNotFoundException when missing, like ReadAllBytes
        if (length > MaxMetadataBytes)
        {
            throw new IOException($"\"{path}\" is {length} bytes; EGL metadata files are never that large.");
        }

        return ReadShared(path);
    }

    /// <summary>
    /// Reads with FileShare.ReadWrite|Delete: <c>File.ReadAllBytes</c> demands FileShare.Read and fails while EGL (running)
    /// has the .item open for writing, which silently dropped a real install from detection.
    /// </summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > MaxMetadataBytes)
        {
            throw new IOException($"\"{path}\" is {fs.Length} bytes; EGL metadata files are never that large.");
        }

        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    private byte[] ReadStrict(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length > MaxMetadataBytes)
        {
            throw new GliderException(Errors.EglWriteFailed($"\"{path}\" is too large to be an EGL manifest; Glider won't write next to it."));
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ReadShared(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not FileNotFoundException)
            {
                if (attempt >= _options.WriteAttempts)
                {
                    throw new GliderException(ex is UnauthorizedAccessException ? Errors.EglAccessDenied(path) : Errors.EglFileLocked(path), ex);
                }

                Thread.Sleep(_options.WriteRetryDelay);
            }
        }
    }

    /// <summary>Fortnite game manifests: AppName == Fortnite and MainGameAppName empty or Fortnite (DLC excluded).</summary>
    internal List<ItemFile> FortniteItems(string? dir = null, bool strict = false) => ReadItems(dir ?? _env.ManifestsDir, strict)
        .Where(i => string.Equals(i.Manifest.AppName, FortniteLayout.AppName, StringComparison.OrdinalIgnoreCase) &&
                    (i.Manifest.MainGameAppName.Length == 0 ||
                     string.Equals(i.Manifest.MainGameAppName, i.Manifest.AppName, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    /// <summary>Best .item for a location: complete before incomplete, then most recently written.</summary>
    private static ItemFile? FindIn(IEnumerable<ItemFile> items, string installPath) => items
        .Where(i => EglPath.Same(i.Manifest.InstallLocation, installPath))
        .OrderBy(i => i.Manifest.IsIncompleteInstall)
        .ThenByDescending(i => i.LastWriteUtc)
        .FirstOrDefault();

    internal List<LauncherInstalledEntry> ReadLauncherInstalled()
    {
        var path = _env.LauncherInstalledDat;
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return LauncherInstalledDat.Parse(ReadSmall(path))
                .Where(e => string.Equals(e.AppName, FortniteLayout.AppName, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(e.InstallLocation))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _log.Warn(LogCategory, $"Ignoring unreadable {path}: {ex.Message}");
            return [];
        }
    }

    public Task<EglState> ScanAsync(CancellationToken ct) => Task.Run(() => Scan(ct), ct);

    internal EglState Scan(CancellationToken ct)
    {
        var installed = Directory.Exists(DataDir);
        var running = IsLauncherRunning();
        var items = FortniteItems();
        ct.ThrowIfCancellationRequested();
        var dat = ReadLauncherInstalled();

        var byPath = new Dictionary<string, InstallCandidate>(StringComparer.Ordinal);
        foreach (var group in items.Where(i => i.Manifest.InstallLocation.Length > 0)
                     .GroupBy(i => EglPath.Normalize(i.Manifest.InstallLocation)))
        {
            var best = FindIn(group, group.First().Manifest.InstallLocation)!;
            if (group.Count() > 1)
            {
                _log.Info(LogCategory, $"{group.Count()} EGL manifests point at {best.Manifest.InstallLocation}; using {best.Path}");
            }

            byPath[group.Key] = MakeCandidate(best.Manifest.InstallLocation, best.Manifest.AppVersionString,
                best.Manifest.InstallSize > 0 ? best.Manifest.InstallSize : null);
        }

        foreach (var e in dat)
        {
            var key = EglPath.Normalize(e.InstallLocation);
            if (byPath.TryGetValue(key, out var existing))
            {
                if (existing.Version is null && e.AppVersion is { Length: > 0 })
                {
                    byPath[key] = existing with { Version = e.AppVersion };
                }

                continue;
            }

            byPath[key] = MakeCandidate(e.InstallLocation!, e.AppVersion, null);
        }

        var candidates = byPath.Values
            .OrderByDescending(c => c.ExecutableFound)
            .ThenBy(c => c.InstallPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var record = _records?.Current;
        var own = record is null ? null : FindIn(items, record.InstallPath);
        var busy = running && (record is not null
            ? BusyEvidence(record.InstallPath, items) is not null
            : candidates.Any(c => c.FolderExists && BusyEvidence(c.InstallPath, items) is not null));

        return new EglState(
            LauncherInstalled: installed,
            LauncherRunning: running,
            BusyWithGame: busy,
            Candidates: candidates,
            SyncEnabled: _settings?.Current.EglSync ?? false,
            SyncAvailable: own is not null,
            ManifestPath: own?.Path,
            LastScan: _options.Time.GetUtcNow());
    }

    internal static InstallCandidate MakeCandidate(string location, string? version, long? size, CandidateSource source = CandidateSource.EpicGamesLauncher)
    {
        var folder = Directory.Exists(location);
        var exe = folder && FortniteLayout.LooksLikeInstall(location);
        return new InstallCandidate(
            Id: EglPath.CandidateId(location),
            Source: source,
            InstallPath: location,
            Version: string.IsNullOrWhiteSpace(version) ? null : version,
            FolderExists: folder,
            ExecutableFound: exe,
            IsStale: !exe,
            SizeBytes: size,
            Reason: exe ? null : folder
                ? "The game files are missing from this folder."
                : "This folder doesn't exist. The drive may be disconnected, or the game was moved or deleted.");
    }

    public EglInstall? FindByLocation(string installPath) => FindIn(FortniteItems(), installPath)?.ToInstall();

    /// <summary>Predicts what <c>legendary import</c> will do with this folder (no writes).</summary>
    public EglAdoptionInfo InspectForAdoption(string installPath)
    {
        var egstore = Path.Combine(installPath, ".egstore");
        string? guid = null;
        var hasMancpn = false;
        if (Directory.Exists(egstore))
        {
            foreach (var f in SafeEnumerate(egstore, "*.mancpn"))
            {
                try
                {
                    var raw = EglJson.TryParseObject(ReadSmall(f), out _);
                    // Legendary compares mancpn['AppName'] == game.app_name exactly.
                    if (raw?.GetValueOrDefault("AppName") is string app && app == FortniteLayout.AppName)
                    {
                        hasMancpn = true;
                        guid = Path.GetFileNameWithoutExtension(f);
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warn(LogCategory, $"Can't read {f}", ex);
                }
            }
        }

        var hasManifest = guid is not null && File.Exists(Path.Combine(egstore, guid + ".manifest"));
        var staging = Directory.Exists(Path.Combine(egstore, "bps")) || DirectoryHasEntries(Path.Combine(egstore, "Pending"));
        var legendaryVerifies = !hasMancpn || staging;
        return new EglAdoptionInfo(
            HasEglMetadata: hasMancpn,
            HasEglManifest: hasManifest,
            ManifestGuid: guid,
            StagingPresent: staging,
            LegendaryWillRequireVerification: legendaryVerifies,
            VerifyRecommended: legendaryVerifies || !hasManifest,
            EglVersion: FindByLocation(installPath)?.Version);
    }

    // =============================================== busy ===============================================

    public bool IsLauncherRunning()
    {
        try
        {
            return _processes.IsRunning(_options.LauncherProcessName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _log.Warn(LogCategory, "Process lookup failed; assuming EGL is not running", ex);
            return false;
        }
    }

    public bool IsBusyWith(string installPath) => GetBusyReason(installPath) is not null;

    /// <summary>Human reason EGL is busy with the install, or null. Requires EGL to be running.</summary>
    public string? GetBusyReason(string installPath) =>
        IsLauncherRunning() ? BusyEvidence(installPath, FortniteItems()) : null;

    /// <summary>File-system evidence of an EGL operation on the install (ignores whether EGL runs).</summary>
    internal string? BusyEvidence(string installPath, IReadOnlyCollection<ItemFile> items)
    {
        if (FindIn(items, installPath) is { Manifest.IsIncompleteInstall: true })
        {
            return InstallingReason;
        }

        var egstore = Path.Combine(installPath, ".egstore");
        if (RecentlyTouched(Path.Combine(egstore, "bps")) || RecentlyTouched(Path.Combine(egstore, "Pending")))
        {
            return UpdatingReason;
        }

        if (Directory.Exists(PendingManifestsDir))
        {
            var pending = FortniteItems(PendingManifestsDir).Where(i => EglPath.Same(i.Manifest.InstallLocation, installPath));
            if (pending.Any(i => IsRecent(i.LastWriteUtc)))
            {
                return UpdatingReason;
            }
        }

        return null;
    }

    private bool IsRecent(DateTime utc) => _options.Time.GetUtcNow().UtcDateTime - utc <= _options.BusyRecentWindow;

    /// <summary>Non-empty directory whose own timestamp or any of its first N entries was written recently.</summary>
    private bool RecentlyTouched(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists)
            {
                return false;
            }

            var any = false;
            var inspected = 0;
            var queue = new Queue<DirectoryInfo>();
            queue.Enqueue(info);
            while (queue.Count > 0 && inspected < _options.MaxStagingEntriesInspected)
            {
                foreach (var entry in queue.Dequeue().EnumerateFileSystemInfos())
                {
                    any = true;
                    if (IsRecent(entry.LastWriteTimeUtc))
                    {
                        return true;
                    }

                    if (entry is DirectoryInfo sub)
                    {
                        queue.Enqueue(sub);
                    }

                    if (++inspected >= _options.MaxStagingEntriesInspected)
                    {
                        break;
                    }
                }
            }

            return any && IsRecent(info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't look inside while EGL holds it: treat as busy only if the folder itself is fresh.
            return Directory.Exists(dir) && IsRecent(Directory.GetLastWriteTimeUtc(dir));
        }
    }

    public async Task WaitUntilNotBusyAsync(string installPath, Action<string?> onBlocked, CancellationToken ct)
    {
        var reason = GetBusyReason(installPath);
        if (reason is null)
        {
            return;
        }

        using var signal = new SemaphoreSlim(0, 1);
        void Wake()
        {
            try
            {
                if (signal.CurrentCount == 0)
                {
                    signal.Release();
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SemaphoreFullException)
            {
                // racing wake-ups after we stopped waiting are harmless
            }
        }

        var watchers = CreateWatchers(installPath, Wake);
        try
        {
            string? last = null;
            while (reason is not null)
            {
                if (reason != last)
                {
                    _log.Info(LogCategory, $"Waiting for EGL ({reason}) on {installPath}");
                    onBlocked(reason);
                    last = reason;
                }

                await signal.WaitAsync(_options.BusyPollInterval, ct).ConfigureAwait(false);
                reason = GetBusyReason(installPath);
            }

            _log.Info(LogCategory, $"EGL is no longer busy with {installPath}");
            onBlocked(null);
        }
        finally
        {
            foreach (var w in watchers)
            {
                w.Dispose();
            }
        }
    }

    private List<FileSystemWatcher> CreateWatchers(string installPath, Action wake)
    {
        var list = new List<FileSystemWatcher>();
        void Add(string dir, string filter, bool subdirs)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            try
            {
                var w = new FileSystemWatcher(dir, filter)
                {
                    IncludeSubdirectories = subdirs,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                w.Changed += (_, _) => wake();
                w.Created += (_, _) => wake();
                w.Deleted += (_, _) => wake();
                w.Renamed += (_, _) => wake();
                w.EnableRaisingEvents = true;
                list.Add(w);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                _log.Debug(LogCategory, $"No watcher on {dir} ({ex.Message}); polling only");
            }
        }

        // .egstore top level only: bps/Pending appearing or disappearing (their contents churn during downloads).
        Add(Path.Combine(installPath, ".egstore"), "*", subdirs: false);
        Add(_env.ManifestsDir, "*.item", subdirs: true);
        return list;
    }

    // =============================================== drift ===============================================

    public DriftResult CheckDrift(GameRecord record)
    {
        var items = FortniteItems();
        var matches = items.Where(i => EglPath.Same(i.Manifest.InstallLocation, record.InstallPath)).ToList();
        if (matches.Count == 0)
        {
            return new DriftResult(DriftKind.EglMissing, null, record.Version, null);
        }

        var best = FindIn(matches, record.InstallPath)!;
        var egl = best.ToInstall();
        DriftResult Result(DriftKind k) => new(k, egl.Version, record.Version, egl);

        if (matches.Select(m => m.Guid.ToUpperInvariant()).Distinct().Count() > 1)
        {
            _log.Warn(LogCategory, $"Several EGL installs claim {record.InstallPath}");
            return Result(DriftKind.Unreconcilable);
        }

        if (record.EglInstallationGuid is { Length: > 0 } ours &&
            !string.Equals(ours, egl.InstallationGuid, StringComparison.OrdinalIgnoreCase))
        {
            return Result(DriftKind.Unreconcilable); // EGL re-installed the folder as a different install
        }

        if (egl.IsIncomplete)
        {
            return Result(DriftKind.Unreconcilable);
        }

        return FortniteVersion.Compare(egl.Version, record.Version) switch
        {
            null => Result(DriftKind.Unreconcilable),
            0 => Result(DriftKind.InSync),
            > 0 => Result(DriftKind.EglNewer),
            _ => Result(DriftKind.GliderNewer),
        };
    }

    // =============================================== write-back ===============================================

    /// <summary>Returns the EGL InstallationGuid used (store it in <see cref="GameRecord.EglInstallationGuid"/>).</summary>
    public Task<string> WriteBackAsync(EglWriteBack data, CancellationToken ct) => Task.Run(async () =>
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await WriteBackCoreAsync(data, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }, ct);

    private async Task<string> WriteBackCoreAsync(EglWriteBack data, CancellationToken ct)
    {
        if (!Directory.Exists(_env.ManifestsDir))
        {
            // Legendary: ValueError('EGS ProgramData path does not exist'). We never create EGL's folders.
            throw new GliderException(Errors.EglWriteFailed($"EGL manifests folder not found: {_env.ManifestsDir}"));
        }

        var installPath = EglPath.TrimTrailingSeparators(data.InstallPath);
        if (!Directory.Exists(installPath))
        {
            throw new GliderException(Errors.EglWriteFailed($"Install folder not found: {installPath}"));
        }

        if (GetBusyReason(installPath) is { } busy)
        {
            throw new GliderException(Errors.EglBusy(busy));
        }

        if (data.LegendaryManifestPath is null || !File.Exists(data.LegendaryManifestPath))
        {
            // Legendary: 'Game Manifest for "…" not found, cannot export!' — without it EGL would see a version whose
            // manifest it doesn't have and re-validate/re-download, so we don't write anything.
            throw new GliderException(Errors.EglWriteFailed($"Legendary manifest not found: {data.LegendaryManifestPath ?? "(none)"}"));
        }

        var items = FortniteItems(strict: true);
        var byLocation = FindIn(items, installPath);
        // The .item EGL already has for this folder wins over the caller's guid: two items for one folder would make
        // EGL show (and manage) the game twice.
        var guid = byLocation?.Guid ?? (data.ExistingInstallationGuid is { Length: > 0 } g ? g : NewGuid());
        var baseItem = byLocation ?? items.FirstOrDefault(i => string.Equals(i.Guid, guid, StringComparison.OrdinalIgnoreCase));
        var itemPath = baseItem?.Path ?? Path.Combine(_env.ManifestsDir, guid + ".item");

        var egstore = Path.Combine(installPath, ".egstore");
        var mancpnPath = Path.Combine(egstore, guid + ".mancpn");
        var existingMancpn = File.Exists(mancpnPath) ? EglJson.TryParseObject(ReadSmall(mancpnPath), out _) : null;

        var manifest = baseItem is null ? EglManifest.Empty() : EglManifest.FromJson(baseItem.Raw);
        ApplyExport(manifest, data, guid, installPath);

        var catalogItemId = data.CatalogItemId ?? manifest.CatalogItemId ?? existingMancpn?.GetValueOrDefault("CatalogItemId") as string ?? "";
        var ns = data.Namespace ?? manifest.CatalogNamespace ?? existingMancpn?.GetValueOrDefault("CatalogNamespace") as string ?? "";
        var mancpn = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["FormatVersion"] = 0L,
            ["AppName"] = data.AppName,
            ["CatalogItemId"] = catalogItemId,
            ["CatalogNamespace"] = ns,
        };

        if (!Directory.Exists(egstore))
        {
            Directory.CreateDirectory(egstore); // Legendary: os.makedirs(egstore_folder)
        }

        var tx = new EglFileTransaction(_paths.EglBackups, _options, _log);
        var manifestBytes = await File.ReadAllBytesAsync(data.LegendaryManifestPath, ct).ConfigureAwait(false);
        tx.Stage(Path.Combine(egstore, guid + ".manifest"), manifestBytes, Path.Combine("egstore", guid + ".manifest"));
        tx.Stage(mancpnPath, PyJson.DumpBytes(mancpn), Path.Combine("egstore", guid + ".mancpn"));
        tx.Stage(itemPath, PyJson.DumpBytes(manifest.ToJson()), Path.Combine("Manifests", Path.GetFileName(itemPath)));
        StageLauncherInstalled(tx, installPath, newLocation: null, newVersion: data.Version);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        _log.Info(LogCategory, $"Exported {data.AppName} {data.Version} to EGL ({itemPath})");
        return guid;
    }

    /// <summary>
    /// Field overrides of Legendary's <c>EGLManifest.from_lgd_game</c> (models/egl.py:129-153), applied on top of the
    /// existing .item. Values Glider doesn't track (BaseURLs, LaunchCommand, bCanRunOffline, …) keep EGL's values.
    /// </summary>
    internal static void ApplyExport(EglManifest m, EglWriteBack data, string guid, string installPath)
    {
        var v = m.Values;
        var folderName = m.GetString("MandatoryAppFolderName");
        var displayName = m.GetString("DisplayName");
        var ownershipToken = m.OwnershipToken;
        if (!string.Equals(m.AppVersionString, data.Version, StringComparison.Ordinal))
        {
            // EGL's own key describing the manifest we're replacing. Legendary's items never carry it (and EGL accepts
            // them), so drop it rather than leave a stale hash next to the new .egstore manifest.
            m.Remainder.Remove("ManifestHash");
        }

        v["AppName"] = data.AppName;
        v["AppVersionString"] = data.Version;
        v["BuildLabel"] = "Live";
        if (data.CatalogItemId is not null)
        {
            v["CatalogItemId"] = data.CatalogItemId;
        }

        if (data.Namespace is not null)
        {
            v["CatalogNamespace"] = data.Namespace;
        }

        v["DisplayName"] = string.IsNullOrEmpty(displayName) ? FortniteLayout.Title : displayName;
        v["InstallLocation"] = installPath;
        v["InstallSize"] = data.InstallSizeBytes;
        if (data.InstallTags.Count > 0)
        {
            // An empty list from Glider means "not chosen" (imported install); writing [] could change EGL's selection.
            v["InstallTags"] = data.InstallTags.Cast<object?>().ToList();
        }

        v["InstallationGuid"] = guid;
        if (data.LaunchExecutable is not null)
        {
            v["LaunchExecutable"] = data.LaunchExecutable;
        }

        v["MainGameAppName"] = data.AppName;
        v["MandatoryAppFolderName"] = string.IsNullOrEmpty(folderName) ? EglPath.LeafName(installPath) : folderName;
        SetLocation(m, installPath);
        v["OwnershipToken"] = ownershipToken; // bool => "true"/"false", as from_lgd_game's igame.requires_ot
        v["bIsIncompleteInstall"] = false;
        v["bNeedsValidation"] = false;
    }

    private static void SetLocation(EglManifest m, string installPath)
    {
        m.Values["InstallLocation"] = installPath;
        m.Values["ManifestLocation"] = $"{installPath}/.egstore";     // f'{igame.install_path}/.egstore'
        m.Values["StagingLocation"] = $"{installPath}/.egstore/bps";  // f'{igame.install_path}/.egstore/bps'
    }

    public Task UpdateLocationAsync(string installationGuid, string newInstallPath, CancellationToken ct) => Task.Run(async () =>
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await UpdateLocationCoreAsync(installationGuid, newInstallPath, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }, ct);

    private async Task UpdateLocationCoreAsync(string installationGuid, string newInstallPath, CancellationToken ct)
    {
        var item = FortniteItems(strict: true).FirstOrDefault(i => string.Equals(i.Guid, installationGuid, StringComparison.OrdinalIgnoreCase))
                   ?? throw new GliderException(Errors.EglWriteFailed($"No EGL manifest with InstallationGuid {installationGuid}"));
        var newPath = EglPath.TrimTrailingSeparators(newInstallPath);
        var oldPath = item.Manifest.InstallLocation;

        foreach (var p in new[] { oldPath, newPath }.Where(p => p.Length > 0))
        {
            if (GetBusyReason(p) is { } busy)
            {
                throw new GliderException(Errors.EglBusy(busy));
            }
        }

        if (!FortniteLayout.LooksLikeInstall(newPath))
        {
            _log.Warn(LogCategory, $"Updating EGL location to {newPath}, which doesn't contain the game executable (yet)");
        }

        var m = EglManifest.FromJson(item.Raw);
        var ownershipToken = m.OwnershipToken;
        SetLocation(m, newPath);
        m.Values["OwnershipToken"] = ownershipToken;

        var tx = new EglFileTransaction(_paths.EglBackups, _options, _log);
        tx.Stage(item.Path, PyJson.DumpBytes(m.ToJson()), Path.Combine("Manifests", Path.GetFileName(item.Path)));
        if (oldPath.Length > 0)
        {
            StageLauncherInstalled(tx, oldPath, newLocation: newPath, newVersion: null);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        _log.Info(LogCategory, $"Updated EGL location {oldPath} -> {newPath} ({item.Path})");
    }

    private void StageLauncherInstalled(EglFileTransaction tx, string matchLocation, string? newLocation, string? newVersion)
    {
        var path = _env.LauncherInstalledDat;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var patched = LauncherInstalledDat.Patch(ReadSmall(path), matchLocation, newLocation, newVersion);
            if (patched is not null)
            {
                tx.Stage(path, patched, Path.Combine("UnrealEngineLauncher", Path.GetFileName(path)));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Secondary file: EGL rebuilds it from its manifests. Leave an unreadable one untouched.
            _log.Warn(LogCategory, $"Not updating {path}: {ex.Message}");
        }
    }

    /// <summary>Legendary: <c>str(uuid4()).replace('-', '').upper()</c>.</summary>
    internal static string NewGuid() => Guid.NewGuid().ToString("N").ToUpperInvariant();

    private List<string> SafeEnumerate(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(LogCategory, $"Can't list {dir}", ex);
            return [];
        }
    }

    private static bool DirectoryHasEntries(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // can't look inside => assume something is there (conservative)
        }
    }

    public void Dispose() => _writeLock.Dispose();

    private sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new();
        public LogLevel MinimumLevel { get; set; } = LogLevel.Info;
        public string CurrentFile => "";
        public void Write(LogLevel level, string category, string message, Exception? exception = null) { }
        public string Tail(int maxLines) => "";
    }
}
