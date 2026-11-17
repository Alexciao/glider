using System.Text.Json;
using Glider.Core.Infrastructure;

namespace Glider.Core.Legendary;

/// <summary>Paths and tolerant readers for the files Legendary keeps under LEGENDARY_CONFIG_PATH (legendary.md §1.2).</summary>
internal static class LegendaryFiles
{
    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true };

    public static string InstalledJson(string config) => Path.Combine(config, "installed.json");

    public static string UserJson(string config) => Path.Combine(config, "user.json");

    public static string VersionJson(string config) => Path.Combine(config, "version.json");

    public static string Metadata(string config, string app) => Path.Combine(config, "metadata", CleanFilename(app) + ".json");

    public static string ResumeFile(string config, string app) => Path.Combine(config, "tmp", CleanFilename(app + ".resume"));

    public static string RepairFile(string config, string app) => Path.Combine(config, "tmp", CleanFilename(app + ".repair"));

    public static string SdlCache(string config, string app) => Path.Combine(config, "tmp", CleanFilename(app + ".json"));

    public static string ManifestsDir(string config) => Path.Combine(config, "manifests");

    /// <summary>Legendary's <c>clean_filename</c> (lfs/utils.py): drops <c>&lt;&gt;:"/\|?*</c>.</summary>
    public static string CleanFilename(string name) => string.Concat(name.Where(c => "<>:\"/\\|?*".IndexOf(c, StringComparison.Ordinal) < 0));

    /// <summary>Reads a JSON file Legendary may be rewriting right now (it uses plain json.dump, not atomic writes): retries torn reads.</summary>
    public static async Task<JsonDocument?> ReadJsonAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var bytes = await AtomicFile.TryReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            try
            {
                return JsonDocument.Parse(bytes, DocOptions);
            }
            catch (JsonException) when (attempt < 3)
            {
                await Task.Delay(50 * (attempt + 1), ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public static JsonDocument? ReadJson(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonDocument.Parse(fs, DocOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The last stdout line that parses as a JSON object (Legendary prints JSON on one line; anything else is noise).</summary>
    public static JsonDocument? JsonFromStdout(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].StartsWith('{'))
            {
                try
                {
                    return JsonDocument.Parse(lines[i], DocOptions);
                }
                catch (JsonException)
                {
                }
            }
        }

        return null;
    }

    public static LegendaryInstalledGame? ParseInstalled(JsonElement root, string app)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(app, out var g) || g.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new LegendaryInstalledGame(
            Str(g, "app_name") ?? app,
            Str(g, "install_path") ?? "",
            Str(g, "version") ?? "",
            Long(g, "install_size") ?? 0,
            NullIfEmpty(Str(g, "executable")),
            Bool(g, "needs_verification"),
            NullIfEmpty(Str(g, "egl_guid")),
            Strings(g, "install_tags"),
            Bool(g, "is_dlc"),
            NullIfEmpty(Str(g, "title")),
            !(g.TryGetProperty("can_run_offline", out var off) && off.ValueKind == JsonValueKind.False),
            Bool(g, "requires_ot"),
            NullIfEmpty(Str(g, "platform")),
            ParsePrerequisites(g));
    }

    /// <summary>installed.json <c>prereq_info</c> (core.py:1667-1670): ids, name, path (relative to the install), args (one string).</summary>
    private static PrerequisiteInfo? ParsePrerequisites(JsonElement game)
    {
        if (Obj(game, "prereq_info") is not { } p || NullIfEmpty(Str(p, "path")) is not { } path)
        {
            return null;
        }

        return new PrerequisiteInfo(NullIfEmpty(Str(p, "name")), path, Str(p, "args"), Strings(p, "ids"));
    }

    /// <summary>
    /// user.json read the way `status --offline` reads it (lfs/lgndry.py userdata + cli.py:1612-1616): absent => signed out;
    /// a JSON object with a displayName => signed in. Anything else (empty, torn mid-write, other shapes) is Unclear and
    /// callers fall back to asking Legendary. Only the non-secret identity fields are read.
    /// </summary>
    public static async Task<UserFile> ReadUserAsync(string path, CancellationToken ct)
    {
        byte[]? bytes;
        try
        {
            bytes = await AtomicFile.TryReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new UserFile(UserFileKind.Unclear, null);
        }

        if (bytes is null)
        {
            return new UserFile(UserFileKind.Missing, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes, DocOptions);
            var (name, id) = ParseUser(doc.RootElement);
            return name is { Length: > 0 } ? new UserFile(UserFileKind.SignedIn, new LegendaryAccount(name, id ?? "")) : new UserFile(UserFileKind.Unclear, null);
        }
        catch (JsonException)
        {
            return new UserFile(UserFileKind.Unclear, null);
        }
    }

    /// <summary>
    /// The signed-in session for one direct call to an Epic API (account id, access token, expiry), or null when there is none or the
    /// file is mid-write. The token is a credential: callers keep it in memory for one request and never log it. Glider never refreshes
    /// or writes it; that stays Legendary's job.
    /// </summary>
    public static async Task<EpicSession?> ReadSessionAsync(string path, CancellationToken ct)
    {
        byte[]? bytes;
        try
        {
            bytes = await AtomicFile.TryReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes, DocOptions);
            var root = doc.RootElement;
            var id = Str(root, "account_id");
            var token = Str(root, "access_token");
            var expires = Str(root, "expires_at");
            return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token)
                || !DateTimeOffset.TryParse(expires, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? null
                : new EpicSession(id, token, at);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>user.json (OAuth token response): only the non-secret identity fields are read.</summary>
    public static (string? DisplayName, string? AccountId) ParseUser(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object ? (Str(root, "displayName"), Str(root, "account_id")) : (null, null);

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    public static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var l) ? l : null;

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static IReadOnlyList<string> Strings(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString()!);
            }
        }

        return list;
    }

    public static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

internal enum UserFileKind
{
    Missing,
    SignedIn,
    Unclear,
}

internal readonly record struct UserFile(UserFileKind Kind, LegendaryAccount? Account);

/// <summary>An Epic session read from user.json. <see cref="ToString"/> leaves the token out, so it can't reach a log by accident.</summary>
public sealed record EpicSession(string AccountId, string AccessToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"EpicSession(account {AccountId}, expires {ExpiresAt:O}, token hidden)";
}
