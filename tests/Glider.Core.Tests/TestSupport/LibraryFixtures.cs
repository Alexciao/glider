using System.Text.Json.Nodes;
using Glider.Core.Game;

namespace Glider.Core.Tests;

/// <summary>The v0.1 Fortnite layout, kept for tests (production code reads it from <see cref="GameProfiles.Fortnite"/>).</summary>
internal static class FortniteLayout
{
    public const string AppName = GameProfiles.FortniteAppName;
    public const string Title = "Fortnite";
    public const string ShippingExeRelative = @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe";
    public const string ShippingProcessName = "FortniteClient-Win64-Shipping";

    public static string ShippingExe(string gamePath) =>
        Path.Combine(gamePath, ShippingExeRelative.Replace('\\', Path.DirectorySeparatorChar));

    public static bool LooksLikeInstall(string gamePath) => File.Exists(ShippingExe(gamePath));
}

/// <summary>
/// Writes Legendary's on-disk library (metadata/&lt;app&gt;.json, assets.json, installed.json, entitlements.json) into a
/// config dir, built from the real public catalog responses in Fixtures/catalog (docs/research/fixtures/legendary/catalog-v0.21.1).
/// </summary>
internal sealed class LibraryFixture
{
    public const string AlienApp = "8935bb3e1420443a9789fe01758039a5";
    public const string AlienDlcApp = "6b61b3692dae45be889bf38cb1f9ed84";
    public const string CastlevaniaApp = "d1df808b70104992a53512d64fae933c";
    public const string FortniteCatalog = "catalog-fn-4fe75bbc5a674f4f9b356b5c90567da5.json";
    public const string AlienCatalog = "catalog-df37f065c3f14eadbf011177396e2966-4106b71233fa4b4ba3b04c818fce91d4.json";
    public const string AlienDlcCatalog = "catalog-df37f065c3f14eadbf011177396e2966-57cd8d4cb4524152a57192555063334a.json";
    public const string CastlevaniaCatalog = "catalog-247d4d4b4e424afd9b474334fe450a43-62da4c1440ae445093f1d650c3a5cc8c.json";

    private readonly JsonObject _assets = new() { ["Windows"] = new JsonArray() };
    private readonly JsonObject _installed = [];
    private readonly JsonArray _entitlements = [];

    public LibraryFixture(string config)
    {
        Config = config;
        Directory.CreateDirectory(Path.Combine(config, "metadata"));
    }

    public string Config { get; }

    /// <summary>Whether <see cref="Save"/> also writes installed.json (off when a fake Legendary owns that file).</summary>
    public bool WritesInstalled { get; set; } = true;

    public static string CatalogDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog");

    /// <summary>The raw catalog item (the only value of the fixture's top-level object).</summary>
    public static JsonObject CatalogItem(string file)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(CatalogDir, file)))!.AsObject();
        return root.First().Value!.AsObject();
    }

    /// <summary>metadata/&lt;app&gt;.json exactly as Legendary writes it (Game.__dict__), plus the Windows asset when <paramref name="withAsset"/>.</summary>
    public LibraryFixture Add(string app, JsonObject item, string build = "1.0", bool withAsset = true, Action<JsonObject>? edit = null)
    {
        item = (JsonObject)item.DeepClone();
        edit?.Invoke(item);
        var ns = (string?)item["namespace"] ?? "";
        var id = (string?)item["id"] ?? "";
        var asset = new JsonObject
        {
            ["app_name"] = app,
            ["asset_id"] = "asset-" + app,
            ["build_version"] = build,
            ["catalog_item_id"] = id,
            ["label_name"] = "Live-Windows",
            ["namespace"] = ns,
            ["metadata"] = new JsonObject(),
            ["sidecar_rev"] = 0,
        };
        var meta = new JsonObject
        {
            ["app_name"] = app,
            ["app_title"] = (string?)item["title"] ?? app,
            ["asset_infos"] = withAsset ? new JsonObject { ["Windows"] = asset.DeepClone() } : new JsonObject(),
            ["base_urls"] = new JsonArray(),
            ["metadata"] = item,
            ["sidecar"] = null,
            ["achievements"] = null,
        };
        File.WriteAllText(Path.Combine(Config, "metadata", app + ".json"), meta.ToJsonString());
        var list = _assets["Windows"]!.AsArray();
        foreach (var old in list.Where(a => (string?)a!["app_name"] == app).ToList())
        {
            list.Remove(old);
        }

        if (withAsset)
        {
            list.Add(asset);
        }

        return this;
    }

    public LibraryFixture AddCatalog(string app, string file, string build = "1.0", bool withAsset = true, Action<JsonObject>? edit = null) =>
        Add(app, CatalogItem(file), build, withAsset, edit);

    /// <summary>A minimal synthetic catalog item (title, namespace, categories, optional attributes).</summary>
    public static JsonObject Item(string title, string ns = "ns", string id = "item", string[]? categories = null, Dictionary<string, string>? attributes = null)
    {
        var ca = new JsonObject();
        foreach (var (k, v) in attributes ?? [])
        {
            ca[k] = new JsonObject { ["type"] = "STRING", ["value"] = v };
        }

        var cats = new JsonArray();
        foreach (var c in categories ?? ["games", "applications"])
        {
            cats.Add(new JsonObject { ["path"] = c });
        }

        return new JsonObject
        {
            ["id"] = id,
            ["namespace"] = ns,
            ["title"] = title,
            ["categories"] = cats,
            ["customAttributes"] = ca,
            ["keyImages"] = new JsonArray(),
            ["creationDate"] = "2020-01-01T00:00:00.000Z",
        };
    }

    public LibraryFixture Install(string app, string path, string version, string? executable = null, bool isDlc = false, string? title = null, bool needsVerification = false)
    {
        _installed[app] = new JsonObject
        {
            ["app_name"] = app,
            ["install_path"] = path,
            ["title"] = title ?? app,
            ["version"] = version,
            ["install_size"] = 1_000_000,
            ["executable"] = executable ?? "",
            ["is_dlc"] = isDlc,
            ["can_run_offline"] = true,
            ["needs_verification"] = needsVerification,
            ["install_tags"] = new JsonArray(),
            ["platform"] = "Windows",
            ["egl_guid"] = "",
        };
        return this;
    }

    public LibraryFixture Entitle(string ns, string? entitlementName, string grantDate, string? catalogItemId = null)
    {
        _entitlements.Add(new JsonObject
        {
            ["namespace"] = ns,
            ["entitlementName"] = entitlementName,
            ["catalogItemId"] = catalogItemId,
            ["grantDate"] = grantDate,
        });
        return this;
    }

    /// <summary>Writes assets.json, installed.json and entitlements.json.</summary>
    public LibraryFixture Save()
    {
        File.WriteAllText(Path.Combine(Config, "assets.json"), _assets.ToJsonString());
        if (WritesInstalled)
        {
            File.WriteAllText(Path.Combine(Config, "installed.json"), _installed.ToJsonString());
        }
        File.WriteAllText(Path.Combine(Config, "entitlements.json"), _entitlements.ToJsonString());
        return this;
    }

    /// <summary>The usual starting point: Fortnite (real catalog item) owned, build <paramref name="latest"/>.</summary>
    public static LibraryFixture Fortnite(string config, string latest) =>
        new LibraryFixture(config).AddCatalog(FortniteLayout.AppName, FortniteCatalog, latest);
}
