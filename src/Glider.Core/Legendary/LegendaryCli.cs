using System.Globalization;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;

namespace Glider.Core.Legendary;

public sealed class LegendaryOptions
{
    /// <summary>Kill a download/verify that shows no progress for this long (Legendary retries failed chunks forever).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>status / auth / launch --json / --version.</summary>
    public TimeSpan QuickTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>info --json downloads the full CDN manifest.</summary>
    public TimeSpan InfoTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>import may download the latest manifest and counts every file.</summary>
    public TimeSpan ImportTimeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>Deleting a 100+ GB install can take a while on a slow disk.</summary>
    public TimeSpan UninstallTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Ceiling for the automatic MemoryError retry. Null = <see cref="AutoTune.MaxSharedMemoryMb"/> of this machine.</summary>
    public int? SharedMemoryRetryCeilingMb { get; init; }

    public LegendaryRunnerOptions Runner { get; init; } = new();

    /// <summary>
    /// Version reported without spawning (the bundled exe is SHA-256 pinned by the build, see build/legendary.json).
    /// Null = ask the exe (`--version`), which costs a ~1.3 s PyInstaller start.
    /// </summary>
    public string? PinnedVersion { get; init; } = LegendaryPin.Version;

    /// <summary>Answer `status --offline` from user.json directly when the file is unambiguous (no process).</summary>
    public bool ReadAccountFromUserJson { get; init; } = true;

    /// <summary>Probe Epic's CDNs at job start and pick a good one (docs/download-perf.md §5.1). Skipped when the user chose a CDN or there is no cached manifest.</summary>
    public bool ProbeCdn { get; init; } = true;

    /// <summary>HTTP handler for the CDN probe (tests inject a fake). Null = <see cref="SocketsHttpHandler"/>.</summary>
    public Func<HttpMessageHandler>? CdnProbeHandler { get; init; }

    /// <summary>
    /// The bundled exe is the Glider fork (docs/download-perf.md §5.4): CDN failover inside Legendary, a runtime control file
    /// (<c>--control-file</c>) and the extra " + Glider" progress line. False = stock Legendary 0.21.1.
    /// </summary>
    public bool Fork { get; init; } = LegendaryPin.IsFork;
}

/// <summary>The Legendary version this build bundles (from build/legendary.json, stamped into the assembly at build time).</summary>
public static class LegendaryPin
{
    public static string? Version { get; } = typeof(LegendaryPin).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "LegendaryVersion")?.Value is { Length: > 0 } v ? v : null;

    /// <summary>The pinned exe is the Glider fork ("0.21.1-glider.N"), which understands <c>--control-file</c>.</summary>
    public static bool IsFork => Version?.Contains("-glider", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// <see cref="ILegendary"/> over the bundled legendary.exe, using the recipes of legendary.md §10. Every run is
/// classified from its output (exit codes are unreliable); failures surface as <see cref="GliderError"/>s.
/// </summary>
public sealed partial class LegendaryCli : ILegendary, IDisposable
{
    private const string NotLoggedIn = "<not logged in>";

    /// <summary>
    /// SHA-256 of the bundled legendary.exe 0.21.1-glider.3, the Glider fork (build/legendary.json "exeSha256"; a test keeps them equal).
    /// Only the exe is hashed: it is PyInstaller's bootloader, and the onedir folder's <c>_internal/</c> next to it is covered by the zip's hash at build time.
    /// </summary>
    public const string PinnedSha256 = "e873d7d975d8f83b37532e5684e44c0b5f14dddf30c3feb44125b6c88463c6ba";

    private readonly AppPaths _paths;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly LegendaryOptions _options;

    public LegendaryCli(AppPaths paths, ILog log, LegendaryOptions? options = null, TimeProvider? time = null)
    {
        _paths = paths;
        _log = log;
        _time = time ?? TimeProvider.System;
        _options = options ?? new LegendaryOptions();
        Runner = new LegendaryProcessRunner(paths, log, _time, _options.Runner);
    }

    internal LegendaryProcessRunner Runner { get; }

    private string Config => _paths.LegendaryConfig;

    /// <summary>Folders a recursive delete must never reach, on top of <see cref="DeleteGuard"/>'s system/user folders.</summary>
    private string?[] ProtectedFolders => [_paths.Root, Path.GetDirectoryName(_paths.LegendaryExe)];

    public void Dispose() => Runner.Dispose();

    // ------------------------------------------------------------------ account

    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        if (_options.PinnedVersion is { } pinned)
        {
            // Still surface a missing exe at startup, as the spawn used to.
            return File.Exists(_paths.LegendaryExe) ? pinned : throw new GliderException(Errors.LegendaryMissing($"Not found: {_paths.LegendaryExe}"));
        }

        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Version, ["--version"]) { CaptureStdout = true, Timeout = _options.QuickTimeout }, null, ct);
        // legendary version "0.21.1", codename "Lowlife"
        var line = run.Stdout.Split('\n').FirstOrDefault(l => l.StartsWith("legendary version \"", StringComparison.Ordinal));
        if (line is null)
        {
            throw new GliderException(ErrorClassifier.Classify(LegendaryCommandKind.Version, run) ?? Errors.LegendaryFailed("No version output."));
        }

        var start = line.IndexOf('"', StringComparison.Ordinal) + 1;
        var end = line.IndexOf('"', start);
        return end > start ? line[start..end] : line.Trim();
    }

    public async Task<LegendaryAccount?> GetAccountOfflineAsync(CancellationToken ct)
    {
        if (_options.ReadAccountFromUserJson)
        {
            var user = await LegendaryFiles.ReadUserAsync(LegendaryFiles.UserJson(Config), ct);
            if (user.Kind == UserFileKind.Missing)
            {
                return null; // status --offline: no user.json => "<not logged in>" (cli.py:1612-1616)
            }

            if (user.Kind == UserFileKind.SignedIn)
            {
                return user.Account;
            }

            // Empty, torn, or unexpected shape: let Legendary decide.
        }

        return await GetAccountOfflineViaCliAsync(ct);
    }

    internal async Task<LegendaryAccount?> GetAccountOfflineViaCliAsync(CancellationToken ct)
    {
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Status, ["status", "--offline", "--json"]) { CaptureStdout = true, Timeout = _options.QuickTimeout }, null, ct);
        var parsed = await AccountFromStatusAsync(run, ct);
        return parsed is { } status ? status.Account : throw new GliderException(ErrorClassifier.Classify(LegendaryCommandKind.Status, run)!);
    }

    public async Task<LegendaryAccount?> RefreshSessionAsync(CancellationToken ct)
    {
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Status, ["status", "--json"]) { CaptureStdout = true, UsesLogin = true, Timeout = _options.QuickTimeout }, null, ct);
        if (await AccountFromStatusAsync(run, ct) is { } status)
        {
            return status.Account;
        }

        var f = run.Facts;
        if (f.VersionCheckCrash && !f.LoginFailed && !f.CredentialsInvalid && !f.LoginHttpFailed && !f.NoSavedCredentials)
        {
            // status logs in first, then force-fetches api.legendary.gl (cli.py:1610) unguarded. The Epic session is
            // fine; only Legendary's own API is unreachable. Report the (refreshed) account from user.json.
            _log.Warn("legendary", "api.legendary.gl unreachable during status; session refresh itself succeeded.");
            return await ReadAccountAsync(null, ct);
        }

        throw new GliderException(ErrorClassifier.Classify(LegendaryCommandKind.Status, run)!);
    }

    /// <summary>`auth --token &lt;exchange code&gt;`: a one-time credential, masked in every log line.</summary>
    public async Task<LegendaryAccount> SignInWithExchangeTokenAsync(string exchangeToken, CancellationToken ct)
    {
        var code = exchangeToken?.Trim().Trim('"') ?? "";
        if (code.Length == 0)
        {
            throw new GliderException(Errors.AuthFailed("Empty sign-in code."));
        }

        for (var attempt = 0; ; attempt++)
        {
            var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.AuthCode, ["auth", "--token", code, "--disable-webview"])
            {
                UsesLogin = true,
                LoginPriority = true,
                RedactedArguments = [2],
                Timeout = _options.QuickTimeout,
            }, null, ct);

            if (run.Facts.CredentialsStillValid && run.Facts.SignedInAs is null && attempt == 0)
            {
                // `auth` is a no-op while signed in (cli.py:100-104) and hasn't used the code yet: replace the session.
                _log.Info("legendary", "Replacing the existing Legendary session with the new sign-in.");
                await SignOutAsync(ct);
                continue;
            }

            var error = ErrorClassifier.Classify(LegendaryCommandKind.AuthCode, run);
            if (error is not null || run.Facts.SignedInAs is null)
            {
                throw new GliderException(error ?? Errors.AuthFailed(run.Facts.Detail));
            }

            return await ReadAccountAsync(run.Facts.SignedInAs, ct) ?? new LegendaryAccount(run.Facts.SignedInAs, "");
        }
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.AuthDelete, ["auth", "--delete"]) { Timeout = _options.QuickTimeout }, null, ct);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.AuthDelete, run);
        AtomicFile.TryDelete(LegendaryFiles.UserJson(Config)); // belt and braces: no token may survive sign-out
        if (error is not null && File.Exists(LegendaryFiles.UserJson(Config)))
        {
            throw new GliderException(error);
        }
    }

    // ------------------------------------------------------------------ game info

    public async Task<LegendaryGameInfo> GetGameInfoAsync(string appName, CancellationToken ct)
    {
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Info, ["info", appName, "--json"]) { CaptureStdout = true, UsesLogin = true, Timeout = _options.InfoTimeout }, null, ct);
        using var doc = LegendaryFiles.JsonFromStdout(run.Stdout);
        if (doc is null)
        {
            throw new GliderException(ErrorClassifier.Classify(LegendaryCommandKind.Info, run)!);
        }

        var root = doc.RootElement;
        var game = LegendaryFiles.Obj(root, "game");
        var manifest = LegendaryFiles.Obj(root, "manifest");
        if (game is null)
        {
            throw new GliderException(File.Exists(LegendaryFiles.UserJson(Config))
                ? Errors.LegendaryFailed($"Epic returned no information for {appName}; it may not be on this account.")
                : Errors.NotSignedIn());
        }

        using var metadataDoc = await LegendaryFiles.ReadJsonAsync(LegendaryFiles.Metadata(Config, appName), ct);
        var metadata = metadataDoc is null ? null : LegendaryFiles.Obj(metadataDoc.RootElement, "metadata");
        var tags = await ChosenInstallTagsAsync(appName, ct);
        var (download, install) = Sizes(manifest, tags);

        return new LegendaryGameInfo(
            LegendaryFiles.Str(game.Value, "app_name") ?? appName,
            LegendaryFiles.Str(game.Value, "title") ?? appName,
            LegendaryFiles.Str(game.Value, "version") ?? (manifest is { } m ? LegendaryFiles.Str(m, "build_version") : null) ?? "",
            download,
            install,
            metadata is { } md && LegendaryFiles.Obj(md, "customAttributes") is { } ca && LegendaryFiles.Obj(ca, "FolderName") is { } fn
                ? LegendaryFiles.Str(fn, "value") : null,
            manifest is { } m2 ? LegendaryFiles.NullIfEmpty(LegendaryFiles.Str(m2, "launch_exe")) : null,
            metadata is { } md2 ? LegendaryFiles.Str(md2, "id") : null,
            metadata is { } md3 ? LegendaryFiles.Str(md3, "namespace") : null,
            metadata is { } md4 ? KeyImages(md4) : [],
            manifest is { } m3 ? Prerequisites(m3) : null);
    }

    public async Task<LegendaryInstalledGame?> GetInstalledAsync(string appName, CancellationToken ct)
    {
        using var doc = await LegendaryFiles.ReadJsonAsync(LegendaryFiles.InstalledJson(Config), ct);
        return doc is null ? null : LegendaryFiles.ParseInstalled(doc.RootElement, appName);
    }

    public string? GetInstalledManifestPath(string appName, string version)
    {
        using var doc = LegendaryFiles.ReadJson(LegendaryFiles.InstalledJson(Config));
        var platform = "Windows";
        if (doc is not null && doc.RootElement.TryGetProperty(appName, out var g) && LegendaryFiles.Str(g, "platform") is { Length: > 0 } p)
        {
            platform = p;
        }

        // lgndry.py:241-260: <app>_<platform>_<version>.manifest, falling back to <app>_<version>.manifest
        foreach (var stem in new[] { $"{appName}_{platform}_{version}", $"{appName}_{version}" })
        {
            var path = Path.Combine(LegendaryFiles.ManifestsDir(Config), LegendaryFiles.CleanFilename(stem) + ".manifest");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ long-running operations

    public async Task<LegendaryOutcome> InstallAsync(InstallArgs args, IProgress<LegendaryProgress> progress, CancellationToken ct)
    {
        var before = await GetInstalledAsync(args.AppName, ct);
        if (args.Mode == InstallMode.Install && before is null && string.IsNullOrWhiteSpace(args.BasePath))
        {
            throw new GliderException(Errors.InvalidRequest("Install needs a base path."));
        }

        if (args.Mode != InstallMode.Install && before is null)
        {
            return Fail(Errors.NotInstalled());
        }

        var kind = args.Mode switch
        {
            InstallMode.Update => LegendaryCommandKind.Update,
            InstallMode.Repair => LegendaryCommandKind.Repair,
            _ => LegendaryCommandKind.Install,
        };
        var location = before?.InstallPath ?? args.BasePath!;
        var sharedMemory = args.MaxSharedMemoryMb;
        var repairAndUpdate = false;
        var ceiling = _options.SharedMemoryRetryCeilingMb ?? AutoTune.MaxSharedMemoryMb(AutoTune.TotalMemoryBytes());
        var resources = args.Resources ?? ResourceProfile.Default;
        if (resources.WorkerCap is { } cap && (args.MaxWorkers <= 0 || args.MaxWorkers > cap))
        {
            args = args with { MaxWorkers = cap }; // Background: a few workers are enough for a download that stays out of the way
        }

        // Which CDN: the user's choice, else the job-start probe (needs a cached manifest), else Legendary's default.
        var sink = progress as ILegendaryProgressSink;
        CdnPlan? plan = null;
        CdnHostResult? current = null;
        if (string.IsNullOrWhiteSpace(args.PreferredCdn) && string.IsNullOrWhiteSpace(args.BaseUrl) && _options.ProbeCdn)
        {
            plan = await ProbeCdnAsync(args.AppName, ct);
            if (plan?.Pick is { } pick)
            {
                current = pick;
                args = WithCdn(args, pick);
            }
        }

        var switched = false;
        var attempt = 0;
        while (true)
        {
            var cdnHost = current?.Host ?? args.PreferredCdn?.Trim() ?? (args.BaseUrl is { } baseUrl ? SafeHost(baseUrl) : CdnProbe.ReadBaseUrls(Config, args.AppName).Select(SafeHost).FirstOrDefault());
            var cacheMiB = sharedMemory > 0 ? sharedMemory : 2048;
            sink?.OnRunInfo(new RunInfo(cdnHost, args.MaxWorkers > 0 ? args.MaxWorkers : null, cacheMiB, plan?.ProbeMegabytesPerSecond(cdnHost)));
            var monitor = new DownloadMonitor(_time, sink, cacheMiB, plan?.ProbeMegabytesPerSecond(cdnHost));
            var wasSwitched = switched;
            var command = new LegendaryCommand(kind, InstallArguments(args, before is not null, sharedMemory, repairAndUpdate, _options.Fork ? ControlFile(args.AppName) : null))
            {
                Exclusive = true,
                UsesLogin = true,
                LowPriority = resources.LowPriority,
                Resources = resources,
                DownloadWorkers = args.MaxWorkers > 0 ? args.MaxWorkers : null,
                OnProgress = monitor.OnProgress,
                ControlFile = _options.Fork ? ControlFile(args.AppName) : null,
                StallTimeout = _options.StallTimeout,
                Timeout = Timeout.InfiniteTimeSpan,
                // A failed SDL download makes Legendary fall back to a stale hardcoded tag table (core.py:449-453): a fresh
                // install would miss most files and an update would *delete* them as "untagged". Stop before it starts.
                // Slow for minutes on a CDN the probe liked: stop too, and rerun on the next one (resume keeps finished files).
                AbortWhen = f => f.SdlDownloadFailed || monitor.WantsSwitch(f) && !wasSwitched && plan is not null,
            };
            var run = await RunAsync(command, progress, ct);
            var f = run.Facts;

            if (!switched && plan is not null && current is not null && plan.Next(current.Host) is { } next &&
                ((run.AbortedEarly && monitor.Cdn.ShouldSwitch) || (run.Stalled && !monitor.Disk.IsDiskBound)))
            {
                _log.Warn("cdn", $"{current.Host} {(run.Stalled ? "stalled" : "has been slow for 3 minutes")}; restarting on {next.Host} (finished files are kept).");
                switched = true;
                current = next;
                args = WithCdn(args, next);
                continue;
            }

            if (attempt == 0 && f.SharedMemorySuggestedMiB is { } need)
            {
                var nextSize = (int)Math.Ceiling((need + 128) / 256.0) * 256;
                var currentSize = sharedMemory > 0 ? sharedMemory : 2048;
                if (nextSize > currentSize && nextSize <= ceiling)
                {
                    _log.Info("legendary", $"Download cache too small ({currentSize} MiB); retrying with {nextSize} MiB.");
                    sharedMemory = nextSize;
                    attempt++;
                    continue;
                }
            }

            if (attempt == 0 && kind == LegendaryCommandKind.Repair && f.ManifestMissing && !repairAndUpdate)
            {
                _log.Info("legendary", "Installed manifest missing; repairing against the latest manifest.");
                repairAndUpdate = true;
                attempt++;
                continue;
            }

            if (run.AbortedEarly && f.SdlDownloadFailed)
            {
                return Fail(Errors.Network("Couldn't download the game's file list (SDL data) from api.legendary.gl. Nothing was changed."));
            }

            var error = ErrorClassifier.Classify(kind, run, FreeBytes(location));
            if (error is null && kind != LegendaryCommandKind.Repair && await GetInstalledAsync(args.AppName, ct) is null)
            {
                error = Errors.LegendaryFailed("Legendary finished but did not register the install.");
            }

            if (error is not null)
            {
                _log.Warn("legendary", $"{kind} failed: {error.Code} {error.Detail}");
            }

            return new LegendaryOutcome(error is null, error, f.DownloadSizeBytes, f.InstallSizeBytes);
        }
    }

    /// <summary>Legendary reorders its CDN list for <c>--preferred-cdn</c>; a probed extra host isn't in it and needs <c>--base-url</c> (which replaces the list).</summary>
    private static InstallArgs WithCdn(InstallArgs args, CdnHostResult host) =>
        host.InEpicList ? args with { PreferredCdn = host.Host, BaseUrl = null } : args with { PreferredCdn = null, BaseUrl = host.BaseUrl };

    private static string? SafeHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;

    /// <summary>Where the fork polls for live changes (<c>{"max_active": n, "base_url": url}</c>), written atomically.</summary>
    internal string ControlFile(string appName) => Path.Combine(Config, "tmp", appName + ".control");

    /// <summary>Job-start CDN probe from the cached manifest; null when there is nothing to choose from or anything goes wrong (never blocks an install).</summary>
    private async Task<CdnPlan?> ProbeCdnAsync(string appName, CancellationToken ct)
    {
        try
        {
            var urls = CdnProbe.ReadBaseUrls(Config, appName);
            var manifest = CdnProbe.FindManifest(Config, appName);
            if (urls.Count == 0 || manifest is null)
            {
                _log.Debug("cdn", $"cdn probe skipped for {appName}: {(urls.Count == 0 ? "no CDN list yet" : "no cached manifest yet")}.");
                return null;
            }

            var table = await Task.Run(() => ManifestReader.TryReadChunks(File.ReadAllBytes(manifest)), ct);
            if (table is null)
            {
                _log.Debug("cdn", "cdn probe skipped: the cached manifest has no plain chunk list.");
                return null;
            }

            var chunks = table.PickProbeChunks(8, Environment.TickCount);
            return await new CdnProbe(_log, _options.CdnProbeHandler, _time).ProbeAsync(urls, chunks, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warn("cdn", "cdn probe failed; using Legendary's default CDN.", e);
            return null;
        }
    }

    public async Task<LegendarySizes?> GetUpdateSizesAsync(string appName, CancellationToken ct)
    {
        // No -y: Legendary prints its analysis (cli.py:1043-1050) and then asks "Do you wish to install?". stdin is closed so
        // it can never answer yes, and we stop the process as soon as the summary is complete. Nothing is downloaded or written.
        // Metadata lane: not exclusive with the writer. If a download holds installed.json.lock, Legendary refuses at once
        // ("Failed to acquire installed data lock", exit 0) and the size simply stays unknown until the next check.
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Update, ["update", appName, "--skip-sdl", "--skip-dlcs", "--update-only"])
        {
            UsesLogin = true,
            LowPriority = true,
            Timeout = _options.InfoTimeout,
            AbortWhen = f => f.SummaryComplete,
        }, null, ct);
        var f = run.Facts;
        return f.SummaryComplete && f.DownloadSizeBytes is { } download ? new LegendarySizes(download, f.InstallSizeBytes ?? 0) : null;
    }

    public Task<LegendaryOutcome> VerifyAsync(string appName, IProgress<LegendaryProgress> progress, CancellationToken ct) =>
        VerifyAsync(appName, progress, null, ct);

    public async Task<LegendaryOutcome> VerifyAsync(string appName, IProgress<LegendaryProgress> progress, ResourceProfile? resources, CancellationToken ct)
    {
        resources ??= ResourceProfile.Default;
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Verify, ["verify", appName])
        {
            Exclusive = true,
            LowPriority = resources.LowPriority,
            Resources = resources,
            StallTimeout = _options.StallTimeout,
            Timeout = Timeout.InfiniteTimeSpan,
        }, progress, ct);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.Verify, run);
        var f = run.Facts;
        return new LegendaryOutcome(error is null, error, null, null, new VerifyCounts(f.VerifyFilesChecked, f.VerifyCorrupted, f.VerifyMissing));
    }

    public async Task SetInstallTagsAsync(string appName, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var value = LegendaryConfigFile.InstallTagsValue(tags);
        if (value.Length == 0)
        {
            return;
        }

        await Runner.SetGameConfigAsync(appName, [new("install_tags", value)], ct).ConfigureAwait(false);
        _log.Info("legendary", $"Recorded the install tags of {appName} ({value.Split(',').Length} packs).");
    }

    public async Task<LegendaryOutcome> ImportAsync(string appName, string gamePath, CancellationToken ct)
    {
        var path = FolderMover.Normalize(gamePath);
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Import, ["-y", "import", appName, path, "--skip-dlcs"])
        {
            Exclusive = true,
            UsesLogin = true,
            Timeout = _options.ImportTimeout,
        }, null, ct);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.Import, run);
        var installed = error is null ? await GetInstalledAsync(appName, ct) : null;
        if (error is null && installed is null)
        {
            error = Errors.LegendaryFailed("Legendary finished the import but did not register the install.");
        }

        return new LegendaryOutcome(error is null, error, null, installed?.InstallSizeBytes);
    }

    public async Task<LegendaryOutcome> MoveAsync(string appName, string newBasePath, bool skipMove, IProgress<LegendaryProgress>? progress, CancellationToken ct)
    {
        var installed = await GetInstalledAsync(appName, ct);
        if (installed is null)
        {
            return Fail(Errors.NotInstalled());
        }

        var newBase = FolderMover.Normalize(newBasePath);
        var source = FolderMover.Normalize(installed.InstallPath);
        var destination = Path.Combine(newBase, Path.GetFileName(source));
        var sink = progress as ILegendaryProgressSink;
        sink?.OnPhase(JobPhase.Moving);

        if (skipMove)
        {
            return await MoveMetadataAsync(appName, newBase, skipMove: true, ct);
        }

        if (FolderMover.PathsEqual(source, destination))
        {
            // Also the restart after a crash between `move --skip-move` and deleting the old copy.
            AtomicFile.TryDelete(Path.Combine(destination, FolderMover.PartialMarker));
            return new LegendaryOutcome(true, null, null, installed.InstallSizeBytes);
        }

        if (FolderMover.IsInside(newBase, source))
        {
            return Fail(Errors.InvalidRequest("The destination is inside the current install folder."));
        }

        if (FolderMover.SameVolume(source, newBase))
        {
            var renamed = await MoveMetadataAsync(appName, newBase, skipMove: false, ct);
            if (renamed.Success || !renamed.Error!.Detail.ContainsOrdinal("different drive"))
            {
                return renamed;
            }
        }

        // Cross-volume: copy ourselves, then let Legendary only rewrite its metadata, then remove the original. The
        // original is deleted recursively by us, so it must really be a game folder and nothing bigger.
        if (!Game.GameProfiles.LooksLikeInstall(appName, source, installed.Executable))
        {
            return Fail(Errors.InvalidFolder($"Glider can't find the game in \"{source}\", so it won't copy and delete that folder. Verify or locate the install first."));
        }

        if (DeleteGuard.Refusal(source, ProtectedFolders) is { } unsafeSource)
        {
            return Fail(Errors.InvalidFolder($"Glider won't move \"{source}\" because {unsafeSource}. Move it manually, then locate it in Glider."));
        }

        var marker = Path.Combine(destination, FolderMover.PartialMarker);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            if (!File.Exists(marker))
            {
                return Fail(Errors.PermissionDenied($"\"{destination}\" already exists."));
            }

            // Our own partial copy from an interrupted move: Legendary still points at the source, so discard it.
            _log.Info("legendary", $"Discarding the partial copy left by an interrupted move at \"{destination}\".");
            if (!FolderMover.TryDeleteTree(destination, _log, ProtectedFolders))
            {
                return Fail(Errors.FilesLocked($"Couldn't remove the partial copy at \"{destination}\"."));
            }
        }

        long size;
        try
        {
            size = FolderMover.TreeSize(source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(e is UnauthorizedAccessException ? Errors.PermissionDenied(e.Message) : Errors.FilesLocked(e.Message));
        }

        var free = FreeBytes(newBase);
        if (free is { } freeBytes && freeBytes < size)
        {
            return Fail(Errors.InsufficientSpace(size, freeBytes));
        }

        try
        {
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(marker, $"Partial copy made by Glider while moving {appName}. Safe to delete.", CancellationToken.None);
            await FolderMover.CopyTreeAsync(source, destination, progress, _time, ct);
        }
        catch (Exception e)
        {
            FolderMover.TryDeleteTree(destination, _log, ProtectedFolders);
            if (e is OperationCanceledException)
            {
                throw;
            }

            _log.Error("legendary", "Copying the install failed.", e);
            return Fail(e switch
            {
                UnauthorizedAccessException => Errors.PermissionDenied(e.Message),
                IOException io when IsDiskFull(io) => Errors.InsufficientSpace(size, FreeBytes(newBase) ?? 0, e.Message),
                IOException => Errors.FilesLocked(e.Message),
                _ => Errors.Unknown(e.Message),
            });
        }

        var outcome = await MoveMetadataAsync(appName, newBase, skipMove: true, CancellationToken.None);
        if (!outcome.Success)
        {
            // Discard the copy unless Legendary demonstrably points at it already (a misclassified failure must not
            // leave Legendary pointing at a folder we just deleted). In that case the source is kept too.
            if (await GetInstalledAsync(appName, CancellationToken.None) is not { } now || !FolderMover.PathsEqual(now.InstallPath, destination))
            {
                FolderMover.TryDeleteTree(destination, _log, ProtectedFolders);
                return outcome;
            }

            _log.Warn("legendary", "move --skip-move reported a failure but Legendary now points at the new folder; keeping both copies.");
            AtomicFile.TryDelete(marker);
            return new LegendaryOutcome(true, null, null, size);
        }

        AtomicFile.TryDelete(marker);
        sink?.OnPhase(JobPhase.Removing);
        FolderMover.TryDeleteTree(source, _log, ProtectedFolders);
        return new LegendaryOutcome(true, null, null, size);
    }

    public async Task<LegendaryOutcome> UninstallAsync(string appName, bool keepFiles, CancellationToken ct)
    {
        List<string> args = ["-y", "uninstall", appName, "--skip-uninstaller"];
        if (keepFiles)
        {
            args.Add("--keep-files");
        }

        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Uninstall, args) { Exclusive = true, Timeout = _options.UninstallTimeout }, null, ct);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.Uninstall, run);
        return new LegendaryOutcome(error is null, error, null, null);
    }

    /// <summary>
    /// Launch lane: never takes installed.json.lock, so a running download never delays it; online it passes the auth gate
    /// ahead of everyone else (L§1.8). <c>--offline</c> skips login, the version check and the exchange code.
    /// </summary>
    public Task<LaunchCommand> GetLaunchCommandAsync(string appName, bool skipVersionCheck, string extraArgs, bool offline, CancellationToken ct) =>
        GetLaunchCommandAsync(appName, skipVersionCheck, extraArgs, offline, null, ct);

    public async Task<LaunchCommand> GetLaunchCommandAsync(string appName, bool skipVersionCheck, string extraArgs, bool offline, string? executableOverride, CancellationToken ct)
    {
        List<string> args = ["launch", appName, "--json"];
        if (skipVersionCheck)
        {
            args.Add("--skip-version-check");
        }

        if (offline)
        {
            args.Add("--offline");
        }

        if (!string.IsNullOrWhiteSpace(executableOverride))
        {
            // Relative to the install folder and must exist there (core.py:850-855); Legendary derives the working directory from it.
            args.Add("--override-exe");
            args.Add(executableOverride.Trim().Replace('\\', '/'));
        }

        // User args are appended by us, not passed through Legendary: argparse would swallow anything that looks
        // like one of its own flags (parse_known_args, cli.py:3114), e.g. "-y".
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Launch, args)
        {
            CaptureStdout = true,
            SensitiveStdout = true, // live exchange code
            UsesLogin = !offline,
            LoginPriority = true,
            Timeout = _options.QuickTimeout,
        }, null, ct);
        using var doc = LegendaryFiles.JsonFromStdout(run.Stdout);
        if (doc is null || LegendaryFiles.Str(doc.RootElement, "game_executable") is null)
        {
            var error = ErrorClassifier.Classify(LegendaryCommandKind.Launch, run)!;
            if (offline && error.Code == ErrorCode.LegendaryFailed && !File.Exists(LegendaryFiles.UserJson(Config)))
            {
                // Even `launch --offline` reads the account (name, id) from user.json (core.py:896) and crashes with a TypeError without it.
                error = Errors.NotSignedIn();
            }

            throw new GliderException(error);
        }

        return ParseLaunch(doc.RootElement, CommandLineSplitter.Split(extraArgs));
    }

    // ------------------------------------------------------------------ library (metadata lane)

    /// <summary>`list -T --json`: Legendary refreshes metadata/, assets.json and entitlements.json; its stdout is not parsed.</summary>
    public async Task<LegendaryOutcome> RefreshLibraryAsync(CancellationToken ct)
    {
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.List, ["list", "-T", "--json"])
        {
            UsesLogin = true,
            QuietStdout = true,
            Timeout = _options.InfoTimeout,
        }, null, ct);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.List, run);
        return new LegendaryOutcome(error is null, error, null, null);
    }

    /// <summary>`list-installed --check-updates --json`: one login + one assets request per installed platform; refreshes assets.json.</summary>
    public async Task<LegendaryOutcome> CheckUpdatesAsync(CancellationToken ct)
    {
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.ListInstalled, ["list-installed", "--check-updates", "--json"])
        {
            UsesLogin = true,
            QuietStdout = true,
            Timeout = _options.InfoTimeout,
        }, null, ct);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.ListInstalled, run);
        return new LegendaryOutcome(error is null, error, null, null);
    }

    // ------------------------------------------------------------------ Store web sign-in

    private static readonly System.Text.RegularExpressions.Regex ExchangeCode = new("^[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// `get-token --json` (hidden subcommand): the body of Epic's `GET /account/api/oauth/exchange`. Only <c>code</c> is read, and only
    /// if it is 32 lower-case hex. Never `--bearer` (that prints the access token). stdout is sensitive: not logged, not in the tail.
    /// </summary>
    public async Task<string?> GetWebExchangeCodeAsync(CancellationToken ct)
    {
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Token, ["get-token", "--json"])
        {
            CaptureStdout = true,
            SensitiveStdout = true,
            UsesLogin = true,
            Timeout = _options.QuickTimeout,
        }, null, ct);
        if (run.ExitCode != 0)
        {
            return null;
        }

        using var doc = LegendaryFiles.JsonFromStdout(run.Stdout);
        var code = doc is null ? null : LegendaryFiles.Str(doc.RootElement, "code");
        return code is not null && ExchangeCode.IsMatch(code) ? code : null;
    }

    // ------------------------------------------------------------------ helpers
    internal static IReadOnlyList<string> InstallArguments(InstallArgs args, bool installed, int sharedMemoryMb, bool repairAndUpdate, string? controlFile = null)
    {
        var verb = args.Mode switch
        {
            InstallMode.Update => "update",
            InstallMode.Repair => "repair",
            _ => "install",
        };
        List<string> a = ["-y", verb, args.AppName];
        if (args.Mode == InstallMode.Install && !installed && !string.IsNullOrWhiteSpace(args.BasePath))
        {
            a.Add("--base-path");
            a.Add(FolderMover.Normalize(args.BasePath));
        }

        if (args.InstallTags is { Count: > 0 } tags && args.Mode != InstallMode.Repair)
        {
            // Explicit components. Untagged files ("") are always part of a game; Legendary deletes files whose tags were
            // deselected, so the list must be complete.
            foreach (var tag in tags.Append("").Distinct(StringComparer.Ordinal))
            {
                a.Add("--install-tag");
                a.Add(tag);
            }
        }
        else
        {
            a.Add("--skip-sdl");
        }

        a.Add("--skip-dlcs"); // DLC are separate writer-lane jobs (L§3)
        if (args.Mode == InstallMode.Update)
        {
            a.Add("--update-only");
        }

        if (repairAndUpdate)
        {
            a.Add("--repair-and-update");
        }

        if (args.MaxWorkers > 0)
        {
            a.Add("--max-workers");
            a.Add(args.MaxWorkers.ToString(CultureInfo.InvariantCulture));
        }

        if (sharedMemoryMb > 0)
        {
            a.Add("--max-shared-memory");
            a.Add(sharedMemoryMb.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(args.PreferredCdn) && char.IsAsciiLetterOrDigit(args.PreferredCdn.Trim()[0]))
        {
            a.Add("--preferred-cdn");
            a.Add(args.PreferredCdn.Trim());
        }

        if (!string.IsNullOrWhiteSpace(args.BaseUrl) && Uri.TryCreate(args.BaseUrl, UriKind.Absolute, out var baseUrl) && baseUrl.Scheme is "https" or "http")
        {
            a.Add("--base-url");
            a.Add(baseUrl.ToString().TrimEnd('/'));
        }

        if (controlFile is not null)
        {
            a.Add("--control-file");
            a.Add(controlFile);
        }

        return a;
    }

    internal static LaunchCommand ParseLaunch(JsonElement root, IReadOnlyList<string> extraArgs)
    {
        var directory = LegendaryFiles.Str(root, "game_directory") ?? "";
        var relative = (LegendaryFiles.Str(root, "game_executable") ?? "").Replace('\\', '/').TrimStart('/');
        var exe = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        var workingDirectory = LegendaryFiles.Str(root, "working_directory") is { Length: > 0 } wd
            ? Path.GetFullPath(wd.Replace('/', Path.DirectorySeparatorChar))
            : Path.GetDirectoryName(exe)!;

        var arguments = new List<string>();
        var wrapper = LegendaryFiles.Strings(root, "launch_command");
        var executable = exe;
        if (wrapper.Count > 0)
        {
            executable = wrapper[0];
            arguments.AddRange(wrapper.Skip(1));
            arguments.Add(exe);
        }

        // Legendary's order (cli.py:694-701): game parameters, user parameters, EGL auth parameters.
        arguments.AddRange(LegendaryFiles.Strings(root, "game_parameters"));
        arguments.AddRange(LegendaryFiles.Strings(root, "user_parameters"));
        arguments.AddRange(extraArgs);
        arguments.AddRange(LegendaryFiles.Strings(root, "egl_parameters"));

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (LegendaryFiles.Obj(root, "environment") is { } e)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    env[p.Name] = p.Value.GetString()!;
                }
            }
        }

        return new LaunchCommand(executable, workingDirectory, arguments, env);
    }

    /// <summary>
    /// The tags `install --skip-sdl` will use, so sizes match what will actually be downloaded: the installed tags, else
    /// the saved [app] install_tags, else the cached SDL "__required" pack plus the untagged bucket (cli.py:965-971).
    /// </summary>
    internal async Task<IReadOnlySet<string>?> ChosenInstallTagsAsync(string appName, CancellationToken ct)
    {
        var installed = await GetInstalledAsync(appName, ct);
        if (installed is { InstallTags.Count: > 0 })
        {
            return installed.InstallTags.Append("").ToHashSet(StringComparer.Ordinal);
        }

        var ini = await AtomicFile.TryReadAllBytesAsync(LegendaryConfigFile.PathIn(Config), ct);
        if (ini is not null && IniValue(System.Text.Encoding.UTF8.GetString(ini), appName, "install_tags") is { } saved)
        {
            return saved.Split(',').Select(t => t.Trim()).Append("").ToHashSet(StringComparer.Ordinal);
        }

        using var sdl = await LegendaryFiles.ReadJsonAsync(LegendaryFiles.SdlCache(Config, appName), ct);
        if (sdl is not null && LegendaryFiles.Obj(sdl.RootElement, "data") is { } data && LegendaryFiles.Obj(data, "__required") is { } req)
        {
            return LegendaryFiles.Strings(req, "tags").Append("").ToHashSet(StringComparer.Ordinal);
        }

        return null;
    }

    internal static (long Download, long Install) Sizes(JsonElement? manifest, IReadOnlySet<string>? tags)
    {
        if (manifest is not { } m)
        {
            return (0, 0);
        }

        var download = LegendaryFiles.Long(m, "download_size") ?? 0;
        var install = LegendaryFiles.Long(m, "disk_size") ?? 0;
        if (tags is null)
        {
            return (download, install);
        }

        // Per-tag sums double-count files/chunks carrying several chosen tags, so this errs high (safe for space checks);
        // never exceed the full manifest totals.
        var tagDownload = SumTags(m, "tag_download_size", tags);
        var tagInstall = SumTags(m, "tag_disk_size", tags);
        return (tagDownload is > 0 and var d ? Math.Min(d, download) : download, tagInstall is > 0 and var i ? Math.Min(i, install) : install);
    }

    private static long? SumTags(JsonElement manifest, string property, IReadOnlySet<string> tags)
    {
        if (!manifest.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
        {
            return null;
        }

        long sum = 0;
        foreach (var entry in list.EnumerateArray())
        {
            if (LegendaryFiles.Str(entry, "tag") is { } tag && tags.Contains(tag))
            {
                sum += LegendaryFiles.Long(entry, "size") ?? 0;
            }
        }

        return sum;
    }

    private static List<KeyImage> KeyImages(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("keyImages", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var images = new List<KeyImage>();
        foreach (var img in list.EnumerateArray())
        {
            if (LegendaryFiles.Str(img, "type") is { } type && LegendaryFiles.Str(img, "url") is { } url)
            {
                images.Add(new KeyImage(type, url, LegendaryFiles.Int(img, "width"), LegendaryFiles.Int(img, "height")));
            }
        }

        return images;
    }

    private static PrerequisiteInfo? Prerequisites(JsonElement manifest)
    {
        if (LegendaryFiles.Obj(manifest, "prerequisites") is not { } p)
        {
            return null;
        }

        return new PrerequisiteInfo(LegendaryFiles.Str(p, "name"), LegendaryFiles.Str(p, "path"), LegendaryFiles.Str(p, "args"), LegendaryFiles.Strings(p, "ids"));
    }

    private static string? IniValue(string ini, string section, string key)
    {
        var inSection = false;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line[1..^1] == section;
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var idx = line.IndexOfAny(['=', ':']);
            if (idx > 0 && line[..idx].Trim() == key)
            {
                return line[(idx + 1)..].Trim();
            }
        }

        return null;
    }

    private async Task<LegendaryOutcome> MoveMetadataAsync(string appName, string newBase, bool skipMove, CancellationToken ct)
    {
        List<string> args = ["-y", "move", appName, newBase];
        if (skipMove)
        {
            args.Add("--skip-move");
        }

        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Move, args) { Exclusive = true, Timeout = _options.ImportTimeout }, null, ct);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.Move, run);
        if (error is not null && run.Facts.MoveCrossDrive)
        {
            error = error with { Detail = "different drive" };
        }

        return new LegendaryOutcome(error is null, error, null, null);
    }

    private readonly record struct StatusAccount(LegendaryAccount? Account);

    /// <summary>Null when stdout had no status JSON; otherwise the account (null inside = signed out).</summary>
    private async Task<StatusAccount?> AccountFromStatusAsync(LegendaryRunResult run, CancellationToken ct)
    {
        using var doc = LegendaryFiles.JsonFromStdout(run.Stdout);
        if (doc is null || LegendaryFiles.Str(doc.RootElement, "account") is not { } account)
        {
            return null;
        }

        if (account == NotLoggedIn)
        {
            return new StatusAccount(null);
        }

        return new StatusAccount(await ReadAccountAsync(account, ct) ?? new LegendaryAccount(account, ""));
    }

    private async Task<LegendaryAccount?> ReadAccountAsync(string? displayName, CancellationToken ct)
    {
        using var doc = await LegendaryFiles.ReadJsonAsync(LegendaryFiles.UserJson(Config), ct);
        if (doc is null)
        {
            return null;
        }

        var (name, id) = LegendaryFiles.ParseUser(doc.RootElement);
        var display = name ?? displayName; // user.json is UTF-8 JSON; stderr/stdout copies of the name are second best
        return display is null ? null : new LegendaryAccount(display, id ?? "");
    }

    private Task<LegendaryRunResult> RunAsync(LegendaryCommand command, IProgress<LegendaryProgress>? progress, CancellationToken ct) =>
        Runner.RunAsync(command, progress, ct);

    private static LegendaryOutcome Fail(GliderError error) => new(false, error, null, null);

    private static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 112 or 39; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
}

internal static class StringExtensions
{
    public static bool ContainsOrdinal(this string? s, string value) => s is not null && s.Contains(value, StringComparison.Ordinal);
}
