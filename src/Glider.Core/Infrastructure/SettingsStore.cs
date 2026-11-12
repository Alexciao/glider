using System.Text.Json;
using System.Text.Json.Nodes;
using Glider.Core.Contracts;

namespace Glider.Core.Infrastructure;

/// <summary>
/// state/settings.json. Unknown/invalid fields fall back to <see cref="Settings.Default"/> one field at a time,
/// so a file from an older or newer build never resets everything. Values are clamped on load and on update.
/// </summary>
public sealed class SettingsStore : ISettingsStore, IDisposable
{
    public const int MinUpdateIntervalMinutes = 5;
    public const int MaxUpdateIntervalMinutes = 24 * 60;
    public const int MaxLaunchArgsLength = 4096;

    private readonly JsonFileStore<Settings> _store;
    private readonly ILog _log;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private Settings _current;

    private SettingsStore(JsonFileStore<Settings> store, ILog log, Settings current)
    {
        _store = store;
        _log = log;
        _current = current;
    }

    public Settings Current => Volatile.Read(ref _current);

    public event Action<Settings>? Changed;

    public static string PathFor(AppPaths paths) => Path.Combine(paths.State, "settings.json");

    public void Dispose()
    {
        _updateLock.Dispose();
        _store.Dispose();
    }

    public static async Task<SettingsStore> LoadAsync(AppPaths paths, ILog log, CancellationToken ct = default)
    {
        var store = new JsonFileStore<Settings>(PathFor(paths), GliderJsonContext.Default.Settings, log);
        var raw = await store.ReadRawAsync(ct).ConfigureAwait(false);
        var settings = raw is null ? Settings.Default : Parse(raw, log);
        return new SettingsStore(store, log, Normalize(settings));
    }

    public async Task<Settings> UpdateAsync(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings);
        await _updateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (normalized == Current)
            {
                return normalized;
            }

            await _store.SaveAsync(normalized).ConfigureAwait(false);
            Volatile.Write(ref _current, normalized);
        }
        finally
        {
            _updateLock.Release();
        }

        _log.Info("settings", "Settings updated.");
        Changed?.Invoke(normalized);
        return normalized;
    }

    /// <summary>Clamps and cleans every field. Pure; used on load and before every save.</summary>
    public static Settings Normalize(Settings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var d = Settings.Default;
        return s with
        {
            DownloadWorkers = s.DownloadWorkers is { } w ? Math.Clamp(w, AutoTune.MinWorkers, AutoTune.MaxWorkers) : null,
            SharedMemoryMb = s.SharedMemoryMb is { } m ? Math.Clamp(m, AutoTune.MinSharedMemoryMb, AutoTune.MaxSharedMemoryCeilingMb) : null,
            PreferredCdn = CleanHost(s.PreferredCdn),
            UpdateCheckIntervalMinutes = Math.Clamp(s.UpdateCheckIntervalMinutes, MinUpdateIntervalMinutes, MaxUpdateIntervalMinutes),
            LaunchArgs = CleanArgs(s.LaunchArgs),
            LogLevel = Enum.IsDefined(s.LogLevel) ? s.LogLevel : d.LogLevel,
            Theme = Enum.IsDefined(s.Theme) ? s.Theme : d.Theme,
            DefaultInstallBase = CleanPath(s.DefaultInstallBase),
        };
    }

    /// <summary>Overlays the file's fields onto the defaults, dropping (and logging) any field that doesn't deserialize.</summary>
    internal static Settings Parse(byte[] raw, ILog log)
    {
        JsonObject? file;
        try
        {
            file = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (JsonException e)
        {
            log.Warn("settings", $"settings.json is unreadable, using defaults: {e.Message}");
            return Settings.Default;
        }

        if (file is null)
        {
            return Settings.Default;
        }

        var merged = (JsonObject)JsonSerializer.SerializeToNode(Settings.Default, GliderJsonContext.Default.Settings)!;
        var known = merged.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var result = Settings.Default;
        foreach (var (key, value) in file.ToList())
        {
            if (!known.Contains(key))
            {
                continue;
            }

            var previous = merged[key]?.DeepClone();
            merged[key] = value?.DeepClone();
            try
            {
                result = merged.Deserialize(GliderJsonContext.Default.Settings) ?? result;
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                log.Warn("settings", $"Ignoring invalid setting '{key}': {e.Message}");
                merged[key] = previous;
            }
        }

        return result;
    }

    private static string? CleanHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var h = host.Trim();
        if (h.Length > 253)
        {
            return null;
        }

        foreach (var c in h)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            {
                return null;
            }
        }

        return h;
    }

    private static string CleanArgs(string? args)
    {
        if (string.IsNullOrEmpty(args))
        {
            return "";
        }

        var a = args.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Trim();
        return a.Length > MaxLaunchArgsLength ? a[..MaxLaunchArgsLength] : a;
    }

    private static string? CleanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Trim();
        return Path.IsPathFullyQualified(p) && p.IndexOfAny(Path.GetInvalidPathChars()) < 0 ? p : null;
    }
}
