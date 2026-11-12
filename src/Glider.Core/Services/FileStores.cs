using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glider.Core.Contracts;

namespace Glider.Core.Services;

internal static class SmallFiles
{
    /// <summary>Write-to-temp then rename, so a crash never leaves a half-written file.</summary>
    public static async Task WriteAtomicAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    public static JsonObject? TryReadObject(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>state/onboarding.json: <c>{"completed": true}</c>. Kept out of Settings so "reset settings" doesn't replay onboarding.</summary>
public sealed class FileOnboardingStore : IOnboardingStore
{
    private readonly string _path;

    public FileOnboardingStore(AppPaths paths)
    {
        _path = Path.Combine(paths.State, "onboarding.json");
        Completed = SmallFiles.TryReadObject(_path)?["completed"]?.GetValue<bool>() ?? false;
    }

    public bool Completed { get; private set; }

    public async Task SetCompletedAsync(bool completed)
    {
        Completed = completed;
        await SmallFiles.WriteAtomicAsync(_path, new JsonObject { ["completed"] = completed }.ToJsonString());
    }
}

/// <summary>
/// Downloads the chosen keyImages into cache/art/ (content-addressed by URL hash) and remembers the last
/// result in cache/art.json so the next start shows art without the network.
/// </summary>
public sealed class HttpArtCache : IArtCache
{
    public const string CacheUrlPrefix = "https://app.glider/cache/";
    private const long MaxImageBytes = 25L * 1024 * 1024;

    private static readonly string[] HeroTypes = ["DieselStoreFrontWide", "OfferImageWide", "DieselGameBox"];
    private static readonly string[] TallTypes = ["DieselGameBoxTall", "OfferImageTall", "DieselStoreFrontTall"];
    private static readonly string[] LogoTypes = ["DieselGameBoxLogo"];

    private readonly AppPaths _paths;
    private readonly HttpClient _http;

    public HttpArtCache(AppPaths paths, HttpClient http)
    {
        _paths = paths;
        _http = http;
    }

    private string IndexPath => Path.Combine(_paths.Cache, "art.json");

    public GameArt? LoadCached()
    {
        var o = SmallFiles.TryReadObject(IndexPath);
        if (o is null)
        {
            return null;
        }

        string? Get(string k)
        {
            var url = o[k]?.GetValue<string>();
            if (url is null || !url.StartsWith(CacheUrlPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            var rel = url[CacheUrlPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            return File.Exists(Path.Combine(_paths.Cache, rel)) ? url : null;
        }

        var art = new GameArt(Get("heroUrl"), Get("tallUrl"), Get("logoUrl"));
        return art.HeroUrl is null && art.TallUrl is null && art.LogoUrl is null ? null : art;
    }

    public async Task<GameArt?> RefreshAsync(IReadOnlyList<KeyImage> images, CancellationToken ct)
    {
        var art = new GameArt(
            await FetchAsync(Pick(images, HeroTypes), ct),
            await FetchAsync(Pick(images, TallTypes), ct),
            await FetchAsync(Pick(images, LogoTypes), ct));
        if (art.HeroUrl is null && art.TallUrl is null && art.LogoUrl is null)
        {
            return null;
        }

        var json = new JsonObject { ["heroUrl"] = art.HeroUrl, ["tallUrl"] = art.TallUrl, ["logoUrl"] = art.LogoUrl };
        await SmallFiles.WriteAtomicAsync(IndexPath, json.ToJsonString());
        return art;
    }

    internal static KeyImage? Pick(IReadOnlyList<KeyImage> images, string[] types)
    {
        foreach (var t in types)
        {
            var hit = images.Where(i => string.Equals(i.Type, t, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => (long)(i.Width ?? 0) * (i.Height ?? 0))
                .FirstOrDefault();
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private async Task<string?> FetchAsync(KeyImage? image, CancellationToken ct)
    {
        if (image is null || !Uri.TryCreate(image.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(image.Url)))[..16];
        var ext = Path.GetExtension(uri.AbsolutePath) is { Length: > 1 and <= 5 } x ? x.ToLowerInvariant() : ".jpg";
        var file = $"{hash}{ext}";
        var dir = Path.Combine(_paths.Cache, "art");
        var target = Path.Combine(dir, file);
        var url = $"{CacheUrlPrefix}art/{file}";
        if (File.Exists(target))
        {
            return url;
        }

        try
        {
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxImageBytes)
            {
                return null;
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.LongLength > MaxImageBytes)
            {
                return null;
            }

            Directory.CreateDirectory(dir);
            var tmp = target + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, target, overwrite: true);
            return url;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>Reads JSON-ish values off disk without throwing (folder inspection).</summary>
internal static class JsonFiles
{
    public static string? ReadString(string path, string key)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                }
            }
        }
        catch (Exception)
        {
            // Unreadable or malformed — the caller falls back.
        }

        return null;
    }
}
