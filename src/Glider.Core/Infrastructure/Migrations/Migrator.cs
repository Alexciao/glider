using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Jobs;

namespace Glider.Core.Infrastructure.Migrations;

/// <summary>state/schema.json — the commit marker of a migration, written LAST.</summary>
public sealed class SchemaFile
{
    public int SchemaVersion { get; set; }

    /// <summary>The Glider version whose data was converted (v0.1's "0.1"), kept across later schema bumps; null for a fresh install.</summary>
    public string? MigratedFrom { get; set; }

    /// <summary>When that conversion ran.</summary>
    public DateTimeOffset? MigratedAt { get; set; }

    /// <summary>Every step this data went through, oldest first (a schema-2 marker without it gets its v0.1 step back-filled).</summary>
    public List<SchemaStep>? History { get; set; }
}

/// <summary>One migration step: <c>from</c> is a Glider version ("0.1") or a schema number ("2").</summary>
public sealed record SchemaStep(string From, int To, DateTimeOffset At, string? Note = null);

public enum MigrationStatus
{
    /// <summary>schema.json already says 2 (or newer).</summary>
    UpToDate,
    /// <summary>No older data (first run): schema.json written.</summary>
    Fresh,
    Migrated,
    /// <summary>Something failed: logged, v0.1 files untouched, retried at the next start.</summary>
    Failed,
}

public sealed record MigrationResult(MigrationStatus Status, string? MigratedFrom, string? BackupDirectory, Exception? Error = null);

/// <summary>
/// Versioned migrations of Glider's own data (state and cache in the data root). Runs before any store loads. Each step is
/// idempotent; schema.json is the commit marker and is written last, so a failed or interrupted run is simply retried at the
/// next start. Legendary's folder and webview2\ are never touched.
/// <para>Schema 3 is the data-root move out of Velopack's install root. The move itself runs in the host before Core starts
/// (<see cref="DataRootMover"/>: it has to happen before WebView2, logging or any store opens a file), so by the time this runs
/// the files are already in the new root; v0.1 data is converted to schema 2 there, then the marker records schema 3.</para>
/// </summary>
public static class Migrator
{
    public const int CurrentSchema = DataRoot.Schema;

    public static string SchemaPath(AppPaths paths) => Path.Combine(paths.State, "schema.json");

    public static async Task<MigrationResult> RunAsync(AppPaths paths, ILog log, TimeProvider? time = null, CancellationToken ct = default)
    {
        time ??= TimeProvider.System;
        try
        {
            var schema = ReadSchema(paths);
            if (schema is { SchemaVersion: >= CurrentSchema })
            {
                return new MigrationResult(MigrationStatus.UpToDate, null, null);
            }

            var now = time.GetUtcNow();
            if (schema is null && !MigrationV1ToV2.HasV1Data(paths))
            {
                await WriteSchemaAsync(paths, new SchemaFile { SchemaVersion = CurrentSchema, MigratedFrom = null, MigratedAt = now, History = [] }, ct);
                return new MigrationResult(MigrationStatus.Fresh, null, null);
            }

            var history = schema?.History ?? [];
            if (schema is { History: null, MigratedFrom: { } earlier })
            {
                history.Add(new SchemaStep(earlier, 2, schema.MigratedAt ?? now)); // a schema-2 marker has no history: back-fill its step
            }

            string? backup = null;
            string? convertedFrom = null;
            if (schema is null)
            {
                backup = await MigrationV1ToV2.RunAsync(paths, log, time, ct);
                convertedFrom = MigrationV1ToV2.From;
                history.Add(new SchemaStep(MigrationV1ToV2.From, 2, now, "backup " + Path.GetFileName(backup)));
                log.Info("migration", $"Converted Glider {MigrationV1ToV2.From} data to schema 2; backup in {backup}.");
            }

            var moved = DataRoot.ReadMarker(paths.Root)?.MovedFrom;
            history.Add(new SchemaStep("2", 3, now, moved is null ? "data root " + paths.Root : $"data root moved from {moved} to {paths.Root}"));
            await WriteSchemaAsync(paths, new SchemaFile
            {
                SchemaVersion = CurrentSchema,
                MigratedFrom = schema?.MigratedFrom ?? convertedFrom,
                MigratedAt = schema is null ? now : schema.MigratedAt,
                History = history,
            }, ct);
            log.Info("migration", $"Data is at schema {CurrentSchema} in {paths.Root}.");
            return new MigrationResult(MigrationStatus.Migrated, convertedFrom, backup);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error("migration", "Migrating older Glider data failed; the old files are untouched and it will be retried at the next start.", e);
            return new MigrationResult(MigrationStatus.Failed, null, null, e);
        }
    }

    internal static SchemaFile? ReadSchema(AppPaths paths)
    {
        var bytes = File.Exists(SchemaPath(paths)) ? File.ReadAllBytes(SchemaPath(paths)) : null;
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(bytes, EngineJsonContext.Default.SchemaFile);
        }
        catch (JsonException)
        {
            return null; // a torn marker means "not committed": migrate again
        }
    }

    private static Task WriteSchemaAsync(AppPaths paths, SchemaFile schema, CancellationToken ct) =>
        AtomicFile.WriteAllBytesAsync(SchemaPath(paths), JsonSerializer.SerializeToUtf8Bytes(schema, EngineJsonContext.Default.SchemaFile), ct: ct);
}

/// <summary>
/// Glider v0.1 (Fortnite only) → schema 2 (whole library), docs/v0.2-design.md §6:
///  1. back up every state\* and cache\*.json byte for byte to backups\migration\v0.1-&lt;ts&gt;\ (sizes verified);
///  2. game.json → games.json["Fortnite"] (install record, playtime, last played); v0.1's global launchArgs → Fortnite's
///     GameSettings.LaunchArgs;
///  3. settings.json → Settings v2 (autoUpdate bool → Always|Never, new fields default, defaultInstallBase seeds libraryFolders);
///  4. jobs.json v1 → v2 with appName "Fortnite" (resume state kept; Legendary's tmp\Fortnite.resume untouched);
///  5. onboarding.json: completed if v0.1 completed or an install record existed; art/size caches → per-app caches;
///  6. every new file is written atomically. game.json, window.json, art.json and the .bak files are never touched; files
///     whose name v2 reuses (settings, jobs, onboarding, installed-size) are rewritten only after the backup.
/// Reading tolerates the real v0.1 quirks: UTF-8 BOMs (art.json, installed-size.json, onboarding.json), <c>+</c>
/// escapes, PascalCase <c>step</c>, integer percents.
/// </summary>
internal static class MigrationV1ToV2
{
    public const string From = "0.1";
    public const string App = GameProfiles.FortniteAppName;

    /// <summary>Test hook: throw after the named step ("backup", "games", "settings", "jobs", "onboarding", "caches").</summary>
    internal static string? FailAfter { get; set; }

    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string State(AppPaths p, string name) => Path.Combine(p.State, name);

    private static string Cache(AppPaths p, string name) => Path.Combine(p.Cache, name);

    public static bool HasV1Data(AppPaths paths) =>
        File.Exists(State(paths, "game.json")) ||
        (ReadObject(State(paths, "jobs.json")) is { } jobs && jobs["schemaVersion"] is null) ||
        (ReadObject(State(paths, "settings.json")) is { } settings && settings["schemaVersion"] is null) ||
        (ReadObject(State(paths, "onboarding.json")) is { } onboarding && onboarding["schemaVersion"] is null) ||
        File.Exists(Cache(paths, "art.json")) ||
        (ReadObject(Cache(paths, "installed-size.json")) is { } size && size["schemaVersion"] is null);

    /// <summary>Returns the backup directory.</summary>
    public static async Task<string> RunAsync(AppPaths paths, ILog log, TimeProvider time, CancellationToken ct)
    {
        var backup = await BackupAsync(paths, time, ct);
        Step("backup");

        var game = ReadObject(State(paths, "game.json"));
        var oldSettings = ReadObject(State(paths, "settings.json"));
        var v1Settings = oldSettings is not null && oldSettings["schemaVersion"] is null ? oldSettings : null;

        await MigrateGamesAsync(paths, game, v1Settings, log, ct);
        Step("games");

        if (v1Settings is not null)
        {
            var migrated = MapSettings(v1Settings);
            await WriteAsync(State(paths, "settings.json"), Infrastructure.SettingsStore.ToFile(Infrastructure.SettingsStore.Normalize(migrated)), ct);
        }

        Step("settings");

        if (ReadObject(State(paths, "jobs.json")) is { } jobs && jobs["schemaVersion"] is null)
        {
            var converted = ConvertJobs(jobs, log);
            await WriteAsync(State(paths, "jobs.json"), converted, ct);
        }

        Step("jobs");

        var onboardingPath = State(paths, "onboarding.json");
        var onboarding = ReadObject(onboardingPath);
        if (onboarding is null && File.Exists(onboardingPath))
        {
            // Unreadable v0.1 file: keep its bytes next to it (not only in the backup) before writing the v2 document.
            PreserveUnreadable(onboardingPath, log);
        }

        if (onboarding is null || onboarding["schemaVersion"] is null)
        {
            var completed = (onboarding?["completed"] is JsonValue c && c.TryGetValue<bool>(out var done) && done) || game is not null;
            await WriteAsync(State(paths, "onboarding.json"), new JsonObject
            {
                ["schemaVersion"] = 2,
                ["completed"] = completed,
                ["step"] = completed ? "done" : "welcome",
                ["skipped"] = new JsonArray(),
            }, ct);
        }

        Step("onboarding");

        await MigrateCachesAsync(paths, ct);
        Step("caches");
        return backup;
    }

    private static void Step(string name)
    {
        if (FailAfter == name)
        {
            throw new IOException($"injected migration failure after '{name}'");
        }
    }

    // ------------------------------------------------------------------ backup

    private static async Task<string> BackupAsync(AppPaths paths, TimeProvider time, CancellationToken ct)
    {
        var sources = new List<(string Source, string Relative)>();
        if (Directory.Exists(paths.State))
        {
            sources.AddRange(Directory.EnumerateFiles(paths.State).Select(f => (f, Path.Combine("state", Path.GetFileName(f)))));
        }

        if (Directory.Exists(paths.Cache))
        {
            sources.AddRange(Directory.EnumerateFiles(paths.Cache, "*.json").Select(f => (f, Path.Combine("cache", Path.GetFileName(f)))));
        }

        // A retry after a failed start must not pile up identical copies (review L8): reuse the newest backup that matches byte for byte.
        if (await NewestMatchingBackupAsync(paths, sources, ct) is { } reused)
        {
            return reused;
        }

        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var dir = Path.Combine(paths.MigrationBackups, $"v{From}-{stamp}");
        for (var n = 2; Directory.Exists(dir); n++)
        {
            dir = Path.Combine(paths.MigrationBackups, $"v{From}-{stamp}-{n}");
        }

        foreach (var (source, relative) in sources)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = await File.ReadAllBytesAsync(source, ct);
            await File.WriteAllBytesAsync(target, bytes, ct);
            if (new FileInfo(target).Length != bytes.Length || new FileInfo(source).Length != bytes.Length)
            {
                throw new IOException($"Backup of {source} is incomplete.");
            }
        }

        return dir;
    }

    /// <summary>The newest v0.1 backup holding exactly these files with exactly these bytes, or null.</summary>
    private static async Task<string?> NewestMatchingBackupAsync(AppPaths paths, List<(string Source, string Relative)> sources, CancellationToken ct)
    {
        if (!Directory.Exists(paths.MigrationBackups))
        {
            return null;
        }

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, relative) in sources)
        {
            expected[relative] = await HashAsync(source, ct);
        }

        // Names sort by their UTC timestamp ("v0.1-yyyyMMdd-HHmmss-fff[-n]").
        foreach (var candidate in Directory.EnumerateDirectories(paths.MigrationBackups, $"v{From}-*").OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var files = Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories).ToList();
            if (files.Count != expected.Count)
            {
                continue;
            }

            var match = true;
            foreach (var file in files)
            {
                if (!expected.TryGetValue(Path.GetRelativePath(candidate, file), out var hash) || hash != await HashAsync(file, ct))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(s, ct));
    }

    // ------------------------------------------------------------------ games.json

    private static async Task MigrateGamesAsync(AppPaths paths, JsonObject? game, JsonObject? v1Settings, ILog log, CancellationToken ct)
    {
        var launchArgs = Str(v1Settings, "launchArgs") ?? "";
        if (game is null && launchArgs.Length == 0)
        {
            return;
        }

        // An earlier, interrupted run may have written games.json already: v2 values win, v0.1 fills what's missing.
        var existing = File.Exists(GamesStore.PathFor(paths))
            ? GamesStore.Normalize(JsonSerializer.Deserialize(await File.ReadAllBytesAsync(GamesStore.PathFor(paths), ct), EngineJsonContext.Default.GamesFile), log)
            : System.Collections.Immutable.ImmutableDictionary<string, GameEntry>.Empty;
        var entry = existing.TryGetValue(App, out var e) ? e : GameEntry.Empty;

        InstallRecord? install = null;
        long playtime = 0;
        DateTimeOffset? lastPlayed = null;
        if (game is not null && Str(game, "installPath") is { Length: > 0 } path && Str(game, "version") is { } version)
        {
            install = new InstallRecord(
                App,
                path,
                version,
                Long(game, "installSizeBytes") ?? 0,
                Date(game, "installedAt") ?? DateTimeOffset.UnixEpoch,
                Bool(game, "adoptedFromEgl"),
                Str(game, "eglInstallationGuid") is { Length: > 0 } g ? g : null,
                Bool(game, "needsVerification"));
            playtime = Math.Max(0, Long(game, "playtimeSeconds") ?? 0);
            lastPlayed = Date(game, "lastPlayedAt");
        }

        entry = entry with
        {
            Install = entry.Install ?? install,
            PlaytimeSeconds = Math.Max(entry.PlaytimeSeconds, playtime),
            LastPlayedAt = entry.LastPlayedAt ?? lastPlayed,
            Settings = entry.Settings.LaunchArgs.Length > 0 ? entry.Settings : entry.Settings with { LaunchArgs = Infrastructure.SettingsStore.CleanLaunchArgs(launchArgs) },
        };

        var file = new GamesFile { Games = new Dictionary<string, GameEntry>(existing, StringComparer.Ordinal) { [App] = entry } };
        await AtomicFile.WriteAllBytesAsync(GamesStore.PathFor(paths), JsonSerializer.SerializeToUtf8Bytes(file, EngineJsonContext.Default.GamesFile), ct: ct);
    }

    // ------------------------------------------------------------------ settings.json

    /// <summary>v0.1 Settings (field by field, unknown/invalid ones dropped) → v2 Settings.</summary>
    internal static Settings MapSettings(JsonObject v1)
    {
        var d = Settings.Default;
        var autoUpdate = v1["autoUpdate"] is JsonValue au && au.TryGetValue<bool>(out var on) ? on ? AutoUpdatePolicy.Always : AutoUpdatePolicy.Never : d.AutoUpdate;
        var defaultBase = Str(v1, "defaultInstallBase") is { Length: > 0 } b ? b : null;
        return d with
        {
            DownloadWorkers = Int(v1, "downloadWorkers"),
            SharedMemoryMb = Int(v1, "sharedMemoryMb"),
            PreferredCdn = Str(v1, "preferredCdn"),
            AutoUpdate = autoUpdate,
            AutoUpdateOnlyWhenIdle = BoolOr(v1, "autoUpdateOnlyWhenIdle", d.AutoUpdateOnlyWhenIdle),
            UpdateCheckIntervalMinutes = Int(v1, "updateCheckIntervalMinutes") ?? d.UpdateCheckIntervalMinutes,
            StartWithWindows = BoolOr(v1, "startWithWindows", d.StartWithWindows),
            StartMinimized = BoolOr(v1, "startMinimized", d.StartMinimized),
            CloseToTray = BoolOr(v1, "closeToTray", d.CloseToTray),
            MinimizeToTrayWhilePlaying = BoolOr(v1, "minimizeToTrayWhilePlaying", d.MinimizeToTrayWhilePlaying),
            LogLevel = EnumOr(v1, "logLevel", d.LogLevel),
            Theme = EnumOr(v1, "theme", d.Theme),
            EglSync = BoolOr(v1, "eglSync", d.EglSync),
            Notifications = BoolOr(v1, "notifications", d.Notifications),
            DefaultInstallBase = defaultBase,
            LibraryFolders = defaultBase is null ? [] : [defaultBase],
        };
    }

    // ------------------------------------------------------------------ jobs.json

    /// <summary>
    /// jobs.json v1 → v2: every job belongs to Fortnite. Info/request gain appName + title, info gains the planned phases and
    /// the current phase, progress is mapped to the v2 shape, verify reports gain bytesToFix; step (PascalCase in v0.1),
    /// game folder, verify counts and the progress baseline (the resume state) are kept as they were.
    /// </summary>
    internal static JsonObject ConvertJobs(JsonObject v1, ILog? log)
    {
        var jobs = new JsonArray();
        if (v1["jobs"] is JsonArray list)
        {
            foreach (var node in list)
            {
                if (node is not JsonObject job || job["info"] is not JsonObject info || job["request"] is not JsonObject request)
                {
                    log?.Warn("migration", "jobs.json: dropping an unusable entry.");
                    continue;
                }

                var kind = Str(info, "kind") ?? Str(request, "kind") ?? "verify";
                var kindEnum = Enum.TryParse<JobKind>(kind, ignoreCase: true, out var k) ? k : JobKind.Verify;
                var progress = info["progress"] as JsonObject;
                var phase = Str(progress, "phase");

                var newInfo = new JsonObject
                {
                    ["id"] = Str(info, "id"),
                    ["appName"] = App,
                    ["title"] = App,
                    ["kind"] = kind,
                    ["status"] = Str(info, "status"),
                    ["phases"] = new JsonArray([.. JobPlan.Phases(kindEnum).Select(p => (JsonNode?)JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(p.ToString())))]),
                    ["phase"] = phase,
                    ["progress"] = progress is null ? null : ConvertProgress(progress, Str(info, "id") ?? "", phase ?? "preparing"),
                    ["error"] = ConvertError(info["error"] as JsonObject),
                    ["createdAt"] = info["createdAt"]?.DeepClone(),
                    ["startedAt"] = null,
                    ["finishedAt"] = info["finishedAt"]?.DeepClone(),
                    ["targetPath"] = info["targetPath"]?.DeepClone(),
                    ["fromVersion"] = info["fromVersion"]?.DeepClone(),
                    ["toVersion"] = info["toVersion"]?.DeepClone(),
                    ["blockedReason"] = info["blockedReason"]?.DeepClone(),
                    ["verify"] = info["verify"] is JsonObject v
                        ? new JsonObject
                        {
                            ["filesChecked"] = v["filesChecked"]?.DeepClone() ?? 0,
                            ["filesBad"] = v["filesBad"]?.DeepClone() ?? 0,
                            ["filesFixed"] = v["filesFixed"]?.DeepClone() ?? 0,
                            ["verified"] = v["verified"]?.DeepClone() ?? false,
                            ["bytesToFix"] = 0,
                        }
                        : null,
                    ["outcome"] = null,
                };
                var newRequest = new JsonObject
                {
                    ["appName"] = App,
                    ["kind"] = kind,
                    ["targetPath"] = request["targetPath"]?.DeepClone(),
                    ["uninstallMode"] = request["uninstallMode"]?.DeepClone(),
                    ["fromVersion"] = request["fromVersion"]?.DeepClone(),
                    ["toVersion"] = request["toVersion"]?.DeepClone(),
                    ["title"] = App,
                    ["options"] = null,
                    ["priority"] = false,
                };

                var baseline = job["baseline"] is JsonObject bl ? (JsonObject)bl.DeepClone() : new JsonObject();
                baseline["jobPercent"] ??= baseline["percent"]?.DeepClone() ?? 0;
                jobs.Add(new JsonObject
                {
                    ["info"] = newInfo,
                    ["request"] = newRequest,
                    ["step"] = job["step"]?.DeepClone() ?? "Main",
                    ["createdGameFolder"] = job["createdGameFolder"]?.DeepClone() ?? false,
                    ["gameFolder"] = job["gameFolder"]?.DeepClone(),
                    ["autoRepairAttempted"] = job["autoRepairAttempted"]?.DeepClone() ?? false,
                    ["verifyChecked"] = job["verifyChecked"]?.DeepClone() ?? 0,
                    ["verifyBad"] = job["verifyBad"]?.DeepClone() ?? 0,
                    ["held"] = false,
                    ["baseline"] = baseline,
                });
            }
        }

        var result = new JsonObject { ["schemaVersion"] = PersistedJobFile.CurrentSchema, ["paused"] = false, ["jobs"] = jobs };

        // Fail here (not at the next start) if the converted file doesn't load.
        var check = result.Deserialize(EngineJsonContext.Default.PersistedJobFile) ?? throw new JsonException("converted jobs.json is empty");
        if (check.Jobs.Count != jobs.Count)
        {
            throw new JsonException("converted jobs.json lost entries");
        }

        return result;
    }

    private static JsonObject ConvertProgress(JsonObject p, string jobId, string phase)
    {
        var percent = Double(p, "percent") ?? 0;
        return new JsonObject
        {
            ["jobId"] = Str(p, "jobId") ?? jobId,
            ["appName"] = App,
            ["phase"] = phase,
            ["percent"] = percent,
            ["phasePercent"] = percent,
            ["elapsedSeconds"] = Double(p, "elapsedSeconds") ?? 0,
            ["etaSeconds"] = null,
            ["network"] = phase is "downloading"
                ? new JsonObject
                {
                    ["downloadedBytes"] = Long(p, "downloadedBytes") ?? 0,
                    ["totalBytes"] = Long(p, "totalDownloadBytes") ?? 0,
                    ["bytesPerSecond"] = 0,
                    ["averageBytesPerSecond"] = 0,
                    ["peakBytesPerSecond"] = 0,
                    ["cdn"] = null,
                    ["connections"] = null,
                }
                : null,
            ["disk"] = phase is "downloading" or "moving"
                ? new JsonObject
                {
                    ["writtenBytes"] = Long(p, "writtenBytes") ?? 0,
                    ["totalBytes"] = Math.Max(0, Long(p, "totalWriteBytes") ?? 0),
                    ["bytesPerSecond"] = 0,
                    ["averageBytesPerSecond"] = 0,
                    ["peakBytesPerSecond"] = 0,
                    ["installSizeBytes"] = null,
                }
                : null,
            ["verify"] = null,
            ["filesDone"] = null,
            ["filesTotal"] = null,
            ["mode"] = "balanced",
            ["limiter"] = "unknown",
        };
    }

    private static JsonObject? ConvertError(JsonObject? e) => e is null ? null : new JsonObject
    {
        ["code"] = e["code"]?.DeepClone() ?? "unknown",
        ["title"] = e["title"]?.DeepClone() ?? "",
        ["message"] = e["message"]?.DeepClone() ?? "",
        ["fix"] = e["fix"]?.DeepClone(),
        ["detail"] = e["detail"]?.DeepClone(),
        ["appName"] = App,
    };

    // ------------------------------------------------------------------ caches

    private static async Task MigrateCachesAsync(AppPaths paths, CancellationToken ct)
    {
        if (ReadObject(Cache(paths, "art.json")) is { } art)
        {
            var index = Path.Combine(paths.Cache, "art", "index.json");
            var existing = ReadObject(index) ?? new JsonObject();
            if (existing[App] is null)
            {
                existing[App] = new JsonObject { ["tall"] = Str(art, "tallUrl"), ["wide"] = Str(art, "heroUrl"), ["logo"] = Str(art, "logoUrl") };
                await WriteAsync(index, existing, ct);
            }
        }

        if (ReadObject(Cache(paths, "installed-size.json")) is { } size && size["schemaVersion"] is null &&
            Str(size, "path") is { } path && Str(size, "version") is { } version && Long(size, "bytes") is { } bytes)
        {
            await WriteAsync(Cache(paths, "installed-size.json"), new JsonObject
            {
                ["schemaVersion"] = 2,
                ["games"] = new JsonObject
                {
                    [App] = new JsonObject { ["path"] = path, ["version"] = version, ["bytes"] = bytes, ["measuredAt"] = size["measuredAt"]?.DeepClone() },
                },
            }, ct);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static Task WriteAsync(string path, JsonObject json, CancellationToken ct) =>
        AtomicFile.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(json.ToJsonString(Indented)), ct: ct);

    /// <summary>A JSON object from a v0.1 file (UTF-8 with or without BOM), or null when missing/unreadable.</summary>
    /// <summary>Copies an unreadable v0.1 file to <c>&lt;name&gt;.v0.1-unreadable</c> (never overwriting an earlier copy).</summary>
    internal static void PreserveUnreadable(string path, ILog log)
    {
        var target = path + ".v0.1-unreadable";
        if (!File.Exists(target))
        {
            File.Copy(path, target);
        }

        log.Warn("migration", $"{Path.GetFileName(path)} could not be read; kept a copy as {Path.GetFileName(target)} and used defaults.");
    }

    internal static JsonObject? ReadObject(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            var span = bytes.AsSpan();
            if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                span = span[3..];
            }

            return JsonNode.Parse(span, documentOptions: DocOptions) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l
        : o?[key] is JsonValue d && d.TryGetValue<double>(out var dd) ? (long)dd : null;

    private static double? Double(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static int? Int(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static bool Bool(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static bool BoolOr(JsonObject o, string key, bool fallback) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static T EnumOr<T>(JsonObject o, string key, T fallback)
        where T : struct, Enum =>
        Str(o, key) is { } s && Enum.TryParse<T>(s, ignoreCase: true, out var e) && Enum.IsDefined(e) ? e : fallback;

    private static DateTimeOffset? Date(JsonObject? o, string key) =>
        Str(o, key) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
