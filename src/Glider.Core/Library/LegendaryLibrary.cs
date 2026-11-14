using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Legendary;

namespace Glider.Core.Library;

/// <summary>An entry of a catalog item's <c>dlcItemList</c>.</summary>
public sealed record CatalogDlcRef(string ItemId, string Title, string? EntitlementName, string? AppName, IReadOnlyList<string> Categories);

/// <summary>One owned app as Legendary cached it in <c>metadata/&lt;app&gt;.json</c> (the raw catalog item, flattened).</summary>
public sealed record CatalogItem(
    string AppName,
    string Title,
    string Namespace,
    string CatalogItemId,
    string? Developer,
    string? Description,
    IReadOnlyList<string> Categories,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<KeyImage> KeyImages,
    string? MainGameItemId,
    string? MainGameAppName,
    IReadOnlyList<CatalogDlcRef> Dlc,
    DateTimeOffset? CreationDate,
    DateTimeOffset? ReleaseDate,
    /// <summary><c>asset_infos.Windows.build_version</c> (the latest build Legendary knew when it wrote the file).</summary>
    string? BuildVersion,
    bool HasWindowsAsset,
    string? EntitlementName)
{
    public bool HasCategory(string path) => Categories.Any(c => string.Equals(c, path, StringComparison.OrdinalIgnoreCase));

    public bool IsDlc => MainGameItemId is not null;

    public bool IsMod => HasCategory("mods");

    /// <summary>Hidden content DLC (e.g. Fortnite's "…Content" packs): never shown.</summary>
    public bool IsHidden => HasCategory("hidden");

    public bool IsLaunchableAddon => HasCategory("addons/launchable");

    public string? Attribute(string key) => Attributes.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public string? FolderName => Attribute("FolderName");

    public string? CloudSaveFolder => Attribute("CloudSaveFolder");

    /// <summary>CanRunOffline defaults to true when absent (Legendary core.py:1677).</summary>
    public bool CanRunOffline => !string.Equals(Attribute("CanRunOffline"), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>ThirdPartyManagedApp or ThirdPartyManagedProvider (models/game.py:131-135).</summary>
    public string? ThirdPartyManager => Attribute("ThirdPartyManagedApp") ?? Attribute("ThirdPartyManagedProvider");

    public string? PartnerLinkType => Attribute("partnerLinkType");

    public IReadOnlyList<LaunchOption> LaunchOptions
    {
        get
        {
            var list = new List<(string Key, LaunchOption Option)>();
            foreach (var (key, name) in Attributes)
            {
                if (key.StartsWith("extraLaunchOption_", StringComparison.Ordinal) && key.EndsWith("_Name", StringComparison.Ordinal))
                {
                    var stem = key[..^"_Name".Length];
                    list.Add((stem, new LaunchOption(name, Attribute(stem + "_Args") ?? "")));
                }
            }

            return [.. list.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Option)];
        }
    }
}

/// <summary>Latest build of an app for Windows (<c>assets.json</c>).</summary>
public sealed record AssetInfo(string AppName, string BuildVersion, string? Namespace, string? CatalogItemId);

public sealed record EntitlementInfo(string? Namespace, string? CatalogItemId, string? EntitlementName, DateTimeOffset? GrantDate);

/// <summary>
/// Everything Glider reads from Legendary's config dir to build the library. Null collections mean "unknown" (missing or
/// unreadable file), never "empty": a torn installed.json must not make every game look uninstalled.
/// </summary>
public sealed record LegendaryLibrary(
    ImmutableDictionary<string, CatalogItem> Items,
    ImmutableDictionary<string, AssetInfo>? Assets,
    ImmutableDictionary<string, LegendaryInstalledGame>? Installed,
    IReadOnlyList<EntitlementInfo>? Entitlements)
{
    public static LegendaryLibrary Empty { get; } = new(ImmutableDictionary<string, CatalogItem>.Empty, null, null, null);
}

/// <summary>
/// Reads Legendary's files directly (L§2.5): every <c>metadata/*.json</c>, <c>assets.json</c>, <c>installed.json</c>,
/// <c>entitlements.json</c>. Legendary rewrites them in place without locks, so each parse is retried (3× / 100 ms) and a
/// file that stays unreadable keeps its previous value. Unchanged metadata files (same size and mtime) are not re-parsed.
/// Glider never writes any of them.
/// </summary>
public sealed class LegendaryLibraryReader
{
    public const string Platform = "Windows";

    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true };

    private readonly string _config;
    private readonly ILog? _log;
    private readonly TimeSpan _retryDelay;
    private readonly ConcurrentDictionary<string, (long Length, DateTime Mtime, CatalogItem? Item)> _metadataCache = new(StringComparer.OrdinalIgnoreCase);

    public LegendaryLibraryReader(string configDir, ILog? log = null, TimeSpan? retryDelay = null)
    {
        _config = configDir;
        _log = log;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
    }

    public string MetadataDir => Path.Combine(_config, "metadata");

    public async Task<LegendaryLibrary> ReadAsync(LegendaryLibrary? previous, CancellationToken ct)
    {
        previous ??= LegendaryLibrary.Empty;
        var itemsTask = ReadMetadataAsync(previous.Items, ct);
        var assets = await ReadOrKeepAsync(Path.Combine(_config, "assets.json"), ParseAssets, previous.Assets, ct).ConfigureAwait(false);
        var installed = await ReadOrKeepAsync(LegendaryFiles.InstalledJson(_config), ParseInstalled, previous.Installed, ct, missingIsEmpty: true).ConfigureAwait(false);
        var entitlements = await ReadOrKeepAsync(Path.Combine(_config, "entitlements.json"), ParseEntitlements, previous.Entitlements, ct).ConfigureAwait(false);
        return new LegendaryLibrary(await itemsTask.ConfigureAwait(false), assets, installed, entitlements);
    }

    private async Task<ImmutableDictionary<string, CatalogItem>> ReadMetadataAsync(ImmutableDictionary<string, CatalogItem> previous, CancellationToken ct)
    {
        if (!Directory.Exists(MetadataDir))
        {
            return ImmutableDictionary<string, CatalogItem>.Empty;
        }

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(MetadataDir).EnumerateFiles("*.json").ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Warn("library", "Can't list Legendary's metadata folder; keeping the previous library.", e);
            return previous;
        }

        var result = new ConcurrentDictionary<string, CatalogItem>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (file, token) =>
        {
            var key = file.FullName;
            if (_metadataCache.TryGetValue(key, out var hit) && hit.Length == file.Length && hit.Mtime == file.LastWriteTimeUtc)
            {
                if (hit.Item is { } cached)
                {
                    result[cached.AppName] = cached;
                }

                return;
            }

            using var doc = await ReadJsonAsync(file.FullName, token).ConfigureAwait(false);
            CatalogItem? item = null;
            if (doc is not null)
            {
                item = ParseMetadata(doc.RootElement);
                _metadataCache[key] = (file.Length, file.LastWriteTimeUtc, item);
            }
            else
            {
                // Torn or unreadable: keep what we had for this app (Legendary's next write heals it).
                var stem = Path.GetFileNameWithoutExtension(file.Name);
                item = previous.Values.FirstOrDefault(i => string.Equals(LegendaryFiles.CleanFilename(i.AppName), stem, StringComparison.OrdinalIgnoreCase));
            }

            if (item is not null)
            {
                result[item.AppName] = item;
            }
        }).ConfigureAwait(false);

        return result.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private async Task<T?> ReadOrKeepAsync<T>(string path, Func<JsonElement, T?> parse, T? previous, CancellationToken ct, bool missingIsEmpty = false)
        where T : class
    {
        if (!File.Exists(path))
        {
            return missingIsEmpty ? parse(JsonDocument.Parse("{}").RootElement) : null;
        }

        using var doc = await ReadJsonAsync(path, ct).ConfigureAwait(false);
        if (doc is null)
        {
            _log?.Warn("library", $"{Path.GetFileName(path)} is unreadable right now; keeping the previous data.");
            return previous;
        }

        return parse(doc.RootElement) ?? previous;
    }

    /// <summary>Parse with retry: Legendary may be rewriting the file in place this very moment.</summary>
    private async Task<JsonDocument?> ReadJsonAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            byte[]? bytes;
            try
            {
                bytes = await AtomicFile.TryReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                bytes = null;
            }

            if (bytes is { Length: > 0 })
            {
                try
                {
                    return JsonDocument.Parse(bytes, DocOptions);
                }
                catch (JsonException)
                {
                }
            }
            else if (bytes is null && !File.Exists(path))
            {
                return null;
            }

            if (attempt >= 3)
            {
                return null;
            }

            await Task.Delay(_retryDelay, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ parsers (pure)

    public static CatalogItem? ParseMetadata(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || LegendaryFiles.Str(root, "app_name") is not { Length: > 0 } app)
        {
            return null;
        }

        var md = LegendaryFiles.Obj(root, "metadata") ?? default;
        var assetInfo = LegendaryFiles.Obj(root, "asset_infos") is { } ai ? LegendaryFiles.Obj(ai, Platform) : null;
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (LegendaryFiles.Obj(md, "customAttributes") is { } ca)
        {
            foreach (var p in ca.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object && LegendaryFiles.Str(p.Value, "value") is { } v)
                {
                    attributes[p.Name] = v.Trim();
                }
            }
        }

        string? mainId = null, mainApp = null;
        if (LegendaryFiles.Obj(md, "mainGameItem") is { } main)
        {
            mainId = LegendaryFiles.Str(main, "id");
            mainApp = FirstAppId(main);
        }

        var dlc = new List<CatalogDlcRef>();
        if (md.ValueKind == JsonValueKind.Object && md.TryGetProperty("dlcItemList", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in list.EnumerateArray())
            {
                if (LegendaryFiles.Str(d, "id") is { } id)
                {
                    dlc.Add(new CatalogDlcRef(id, LegendaryFiles.Str(d, "title") ?? id, LegendaryFiles.Str(d, "entitlementName"), FirstAppId(d), Categories(d)));
                }
            }
        }

        return new CatalogItem(
            app,
            LegendaryFiles.Str(root, "app_title") ?? LegendaryFiles.Str(md, "title") ?? app,
            LegendaryFiles.Str(md, "namespace") ?? (assetInfo is { } a1 ? LegendaryFiles.Str(a1, "namespace") : null) ?? "",
            LegendaryFiles.Str(md, "id") ?? (assetInfo is { } a2 ? LegendaryFiles.Str(a2, "catalog_item_id") : null) ?? "",
            LegendaryFiles.NullIfEmpty(LegendaryFiles.Str(md, "developerDisplayName") ?? LegendaryFiles.Str(md, "developer")),
            LegendaryFiles.NullIfEmpty(LegendaryFiles.Str(md, "description")),
            Categories(md),
            attributes,
            KeyImages(md),
            mainId,
            mainApp,
            dlc,
            Date(LegendaryFiles.Str(md, "creationDate")),
            ReleaseDate(md),
            assetInfo is { } a3 ? LegendaryFiles.NullIfEmpty(LegendaryFiles.Str(a3, "build_version")) : null,
            assetInfo is not null,
            LegendaryFiles.Str(md, "entitlementName"));
    }

    public static ImmutableDictionary<string, AssetInfo>? ParseAssets(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var b = ImmutableDictionary.CreateBuilder<string, AssetInfo>(StringComparer.Ordinal);
        if (root.TryGetProperty(Platform, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                if (LegendaryFiles.Str(a, "app_name") is { Length: > 0 } app)
                {
                    b[app] = new AssetInfo(app, LegendaryFiles.Str(a, "build_version") ?? "", LegendaryFiles.Str(a, "namespace"), LegendaryFiles.Str(a, "catalog_item_id"));
                }
            }
        }

        return b.ToImmutable();
    }

    public static ImmutableDictionary<string, LegendaryInstalledGame>? ParseInstalled(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var b = ImmutableDictionary.CreateBuilder<string, LegendaryInstalledGame>(StringComparer.Ordinal);
        foreach (var p in root.EnumerateObject())
        {
            if (LegendaryFiles.ParseInstalled(root, p.Name) is { InstallPath.Length: > 0 } g)
            {
                b[p.Name] = g;
            }
        }

        return b.ToImmutable();
    }

    public static IReadOnlyList<EntitlementInfo>? ParseEntitlements(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<EntitlementInfo>();
        foreach (var e in root.EnumerateArray())
        {
            list.Add(new EntitlementInfo(LegendaryFiles.Str(e, "namespace"), LegendaryFiles.Str(e, "catalogItemId"), LegendaryFiles.Str(e, "entitlementName"),
                Date(LegendaryFiles.Str(e, "grantDate"))));
        }

        return list;
    }

    private static string? FirstAppId(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("releaseInfo", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (var ri in r.EnumerateArray())
            {
                if (LegendaryFiles.Str(ri, "appId") is { Length: > 0 } id)
                {
                    return id;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? ReleaseDate(JsonElement md)
    {
        if (md.ValueKind == JsonValueKind.Object && md.TryGetProperty("releaseInfo", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (var ri in r.EnumerateArray())
            {
                if (Date(LegendaryFiles.Str(ri, "dateAdded")) is { } d)
                {
                    return d;
                }
            }
        }

        return null;
    }

    private static List<string> Categories(JsonElement item)
    {
        var list = new List<string>();
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("categories", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var cat in c.EnumerateArray())
            {
                if (LegendaryFiles.Str(cat, "path") is { } path)
                {
                    list.Add(path);
                }
            }
        }

        return list;
    }

    private static List<KeyImage> KeyImages(JsonElement md)
    {
        var images = new List<KeyImage>();
        if (md.ValueKind == JsonValueKind.Object && md.TryGetProperty("keyImages", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in list.EnumerateArray())
            {
                if (LegendaryFiles.Str(img, "type") is { } type && LegendaryFiles.Str(img, "url") is { } url)
                {
                    images.Add(new KeyImage(type, url, LegendaryFiles.Int(img, "width"), LegendaryFiles.Int(img, "height")));
                }
            }
        }

        return images;
    }

    private static DateTimeOffset? Date(string? s) =>
        s is { Length: > 0 } && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
