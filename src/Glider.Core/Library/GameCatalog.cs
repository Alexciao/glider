using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Glider.Core.Art;
using Glider.Core.Contracts;

namespace Glider.Core.Library;

/// <summary>Live, non-file facts about one game that the summaries carry (owned by the service layer).</summary>
public sealed record GameRuntime(
    RunState RunState = RunState.Idle,
    string? ActiveJobId = null,
    JobKind? ActiveJobKind = null,
    /// <summary>Measured bytes on disk for the current install (null = not measured for this path + version).</summary>
    long? InstalledSizeBytes = null,
    /// <summary>Legendary's dry-run download size for the pending update (null = unknown).</summary>
    long? UpdateDownloadBytes = null,
    /// <summary>The install folder no longer holds the game.</summary>
    bool Broken = false)
{
    public static GameRuntime Idle { get; } = new();
}

/// <summary>Everything besides Legendary's files that the catalog needs.</summary>
public sealed record CatalogContext(
    ImmutableDictionary<string, GameEntry> Glider,
    ImmutableDictionary<string, GameRuntime> Runtime,
    bool CloudSavesEnabled,
    /// <summary>v0.1's cached art (cache.glider URLs): only for apps the catalog has no pictures for.</summary>
    ImmutableDictionary<string, GameArt>? CachedArt = null,
    /// <summary>Cover colours learned by the art cache (valid while their source hash matches), for blur-up.</summary>
    IReadOnlyDictionary<string, ArtPlaceholder>? Placeholders = null)
{
    public static CatalogContext Empty { get; } = new(ImmutableDictionary<string, GameEntry>.Empty, ImmutableDictionary<string, GameRuntime>.Empty, true);

    public GameEntry Entry(string app) => Glider.TryGetValue(app, out var e) ? e : GameEntry.Empty;

    public GameRuntime Run(string app) => Runtime.TryGetValue(app, out var r) ? r : GameRuntime.Idle;
}

/// <summary>
/// Pure mapping from Legendary's cached files + Glider's own records to the library (docs/v0.2-design.md §2, L§2.3):
/// kind from <c>mainGameItem</c> / <c>addons/launchable</c>; hidden content DLC and mods excluded; cloud saves =
/// <c>CloudSaveFolder</c>; offline = <c>CanRunOffline</c> (default true); third party = <c>ThirdPartyManagedApp|Provider</c>
/// (+ <c>partnerLinkType</c>); added = entitlement grant date else creation date; art = <c>DieselGameBoxTall</c> (tile) /
/// <c>DieselGameBox</c> (hero), each falling back to the other, then to a generated placeholder colour.
/// No I/O: everything it needs is in its arguments.
/// </summary>
public static class GameCatalog
{
    public const string TallType = "DieselGameBoxTall";
    public const string WideType = "DieselGameBox";
    public const string LogoType = "DieselGameBoxLogo";

    public static ImmutableDictionary<string, GameSummary> Build(LegendaryLibrary lib, CatalogContext ctx)
    {
        var b = ImmutableDictionary.CreateBuilder<string, GameSummary>(StringComparer.Ordinal);
        var byCatalogId = new Dictionary<string, CatalogItem>(StringComparer.Ordinal);
        foreach (var item in lib.Items.Values)
        {
            if (item.CatalogItemId.Length > 0)
            {
                byCatalogId.TryAdd(item.CatalogItemId, item);
            }
        }

        var visible = lib.Items.Values.Where(i => IsVisible(i, lib)).ToList();

        // Owned DLC per base game: DLC metadata files (asset owned) + entitlement-only DLC from the base's dlcItemList.
        var entitlementNames = lib.Entitlements?.Select(e => e.EntitlementName).Where(n => n is not null).ToHashSet(StringComparer.Ordinal)
                               ?? [];
        var dlcCount = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in visible)
        {
            var summary = Summary(item, BaseAppName(item, byCatalogId), lib, ctx);
            b[summary.AppName] = summary;
            if (summary.BaseAppName is { } baseApp)
            {
                dlcCount[baseApp] = dlcCount.GetValueOrDefault(baseApp) + 1;
            }
        }

        foreach (var item in visible.Where(i => !i.IsDlc))
        {
            foreach (var d in item.Dlc)
            {
                if (d.Categories.Any(c => c is "hidden" or "mods"))
                {
                    continue;
                }

                var app = d.AppName ?? d.ItemId;
                if (b.ContainsKey(app) || lib.Items.ContainsKey(app) || d.EntitlementName is null || !entitlementNames.Contains(d.EntitlementName))
                {
                    continue;
                }

                b[app] = EntitlementOnlyDlc(item, d, app, lib, ctx);
                dlcCount[item.AppName] = dlcCount.GetValueOrDefault(item.AppName) + 1;
            }
        }

        // Installed apps Legendary has no metadata for (metadata pruned or torn): still show them, from installed.json.
        if (lib.Installed is { } installed)
        {
            foreach (var (app, g) in installed)
            {
                if (!b.ContainsKey(app) && !lib.Items.ContainsKey(app))
                {
                    b[app] = Orphan(app, g, null, lib, ctx);
                }
            }
        }
        else
        {
            // installed.json unknown: Glider's own install records keep installed games visible (never "not installed").
            foreach (var (app, entry) in ctx.Glider)
            {
                if (entry.Install is not null && !b.ContainsKey(app) && !lib.Items.ContainsKey(app))
                {
                    b[app] = Orphan(app, null, entry.Install, lib, ctx);
                }
            }
        }

        foreach (var (app, count) in dlcCount)
        {
            if (b.TryGetValue(app, out var s))
            {
                b[app] = s with { DlcCount = count };
            }
        }

        return b.ToImmutable();
    }

    /// <summary>Shown in the library: not mods, not hidden content DLC, and installable for Windows, third-party, or installed.</summary>
    public static bool IsVisible(CatalogItem item, LegendaryLibrary lib)
    {
        if (item.IsMod || item.IsHidden)
        {
            return false;
        }

        if (lib.Assets is null || lib.Assets.ContainsKey(item.AppName) || item.ThirdPartyManager is not null)
        {
            return true;
        }

        return lib.Installed?.ContainsKey(item.AppName) == true;
    }

    public static string? BaseAppName(CatalogItem item, IReadOnlyDictionary<string, CatalogItem> byCatalogId)
    {
        if (!item.IsDlc)
        {
            return null;
        }

        if (item.MainGameAppName is { Length: > 0 } app)
        {
            return app;
        }

        return item.MainGameItemId is { } id && byCatalogId.TryGetValue(id, out var main) ? main.AppName : null;
    }

    public static GameKind KindOf(CatalogItem item) =>
        item.IsLaunchableAddon ? GameKind.LaunchableAddon : item.IsDlc ? GameKind.Dlc : GameKind.Game;

    public static (ThirdPartyStore Store, string? Name) ThirdParty(CatalogItem? item)
    {
        if (item is null)
        {
            return (ThirdPartyStore.None, null);
        }

        var manager = item.ThirdPartyManager;
        if (manager is null)
        {
            return string.Equals(item.PartnerLinkType, "ubisoft", StringComparison.OrdinalIgnoreCase)
                ? (ThirdPartyStore.Ubisoft, "Ubisoft Connect")
                : (ThirdPartyStore.None, null);
        }

        var m = manager.ToLowerInvariant();
        if (m.Contains("origin", StringComparison.Ordinal) || m.Contains("ea app", StringComparison.Ordinal) || m == "ea")
        {
            return (ThirdPartyStore.Ea, "EA app");
        }

        if (m.Contains("ubi", StringComparison.Ordinal) || m.Contains("uplay", StringComparison.Ordinal))
        {
            return (ThirdPartyStore.Ubisoft, "Ubisoft Connect");
        }

        return (ThirdPartyStore.Other, manager);
    }

    private static GameSummary Summary(CatalogItem item, string? baseApp, LegendaryLibrary lib, CatalogContext ctx)
    {
        var app = item.AppName;
        var entry = ctx.Entry(app);
        var run = ctx.Run(app);
        var installed = InstalledOf(app, lib, entry);
        var (store, storeName) = ThirdParty(item);
        var latest = LatestVersion(app, item, lib);
        // Ubisoft partner-linked games install normally (they only launch through Ubisoft Connect); EA/other managed ones don't.
        var installable = item.ThirdPartyManager is null && (lib.Assets is null || lib.Assets.ContainsKey(app));

        var status = installed is not null ? run.Broken ? InstallStatus.Broken : InstallStatus.Installed
            : installable ? InstallStatus.NotInstalled
            : InstallStatus.NotInstallable;
        var update = installed is not null && UpdateAvailable(installed.Version, latest);
        var cloud = item.CloudSaveFolder is not null;

        return new GameSummary(
            app,
            item.Title,
            KindOf(item),
            baseApp,
            item.Namespace,
            item.CatalogItemId,
            item.Developer,
            status,
            installed?.Version,
            latest,
            update,
            installed?.InstallPath,
            installed is { InstallSizeBytes: > 0 } i ? i.InstallSizeBytes : null,
            installed is null ? null : run.InstalledSizeBytes,
            update ? run.UpdateDownloadBytes : null,
            cloud,
            SaveStatus(cloud, ctx.CloudSavesEnabled, entry),
            installed?.CanRunOffline ?? item.CanRunOffline,
            store,
            storeName,
            0,
            entry.Favorite,
            entry.Hidden,
            AddedAt(item, lib),
            entry.LastPlayedAt,
            entry.PlaytimeSeconds,
            ArtFor(item, ctx),
            run.RunState,
            run.ActiveJobId,
            run.ActiveJobKind,
            installed?.SharedWithEgl ?? false,
            installed?.NeedsVerification ?? false);
    }

    private static GameSummary EntitlementOnlyDlc(CatalogItem baseItem, CatalogDlcRef d, string app, LegendaryLibrary lib, CatalogContext ctx)
    {
        var entry = ctx.Entry(app);
        return new GameSummary(
            app, d.Title, d.Categories.Contains("addons/launchable") ? GameKind.LaunchableAddon : GameKind.Dlc, baseItem.AppName,
            baseItem.Namespace, d.ItemId, baseItem.Developer, InstallStatus.NotInstallable, null, null, false, null, null, null, null,
            false, SaveSyncStatus.NotSupported, baseItem.CanRunOffline, ThirdPartyStore.None, null, 0, entry.Favorite, entry.Hidden,
            AddedAt(baseItem, lib), entry.LastPlayedAt, entry.PlaytimeSeconds, ArtFor(baseItem, ctx) with { Placeholder = Placeholder(app) },
            RunState.Idle, null, null, false, false);
    }

    private static GameSummary Orphan(string app, LegendaryInstalledGame? legendary, InstallRecord? record, LegendaryLibrary lib, CatalogContext ctx)
    {
        var entry = ctx.Entry(app);
        var run = ctx.Run(app);
        var installed = InstalledOf(app, lib, entry);
        var latest = lib.Assets?.GetValueOrDefault(app)?.BuildVersion is { Length: > 0 } v ? v : null;
        var update = installed is not null && UpdateAvailable(installed.Version, latest);
        return new GameSummary(
            app, legendary?.Title ?? app, legendary?.IsDlc == true ? GameKind.Dlc : GameKind.Game, null, "", "", null,
            installed is null ? InstallStatus.NotInstalled : run.Broken ? InstallStatus.Broken : InstallStatus.Installed,
            installed?.Version, latest, update, installed?.InstallPath ?? record?.InstallPath,
            installed is { InstallSizeBytes: > 0 } i ? i.InstallSizeBytes : null, installed is null ? null : run.InstalledSizeBytes,
            update ? run.UpdateDownloadBytes : null, false, SaveSyncStatus.NotSupported, installed?.CanRunOffline ?? true, ThirdPartyStore.None,
            null, 0, entry.Favorite, entry.Hidden, null, entry.LastPlayedAt, entry.PlaytimeSeconds,
            ctx.CachedArt?.GetValueOrDefault(app) ?? new GameArt(null, null, null, null, Placeholder(app)),
            run.RunState, run.ActiveJobId, run.ActiveJobKind, installed?.SharedWithEgl ?? false, installed?.NeedsVerification ?? false);
    }

    /// <summary>The install as the summary shows it: Legendary's installed.json when readable, else Glider's own record.</summary>
    public sealed record InstalledView(string Version, string InstallPath, long InstallSizeBytes, bool CanRunOffline, bool SharedWithEgl, bool NeedsVerification, string? Executable);

    public static InstalledView? InstalledOf(string app, LegendaryLibrary lib, GameEntry entry)
    {
        var record = entry.Install;
        if (lib.Installed is { } installed)
        {
            if (!installed.TryGetValue(app, out var g))
            {
                return null;
            }

            return new InstalledView(g.Version, g.InstallPath, g.InstallSizeBytes, g.CanRunOffline,
                record?.EglInstallationGuid is not null || g.EglGuid is not null,
                g.NeedsVerification || (record?.NeedsVerification ?? false), g.Executable);
        }

        return record is null
            ? null
            : new InstalledView(record.Version, record.InstallPath, record.InstallSizeBytes, true, record.EglInstallationGuid is not null, record.NeedsVerification, null);
    }

    public static string? LatestVersion(string app, CatalogItem? item, LegendaryLibrary lib) =>
        lib.Assets?.GetValueOrDefault(app)?.BuildVersion is { Length: > 0 } v ? v : item?.BuildVersion;

    /// <summary>Legendary's own rule (cli.py:647, core.py:1370): any difference between installed and latest build is an update.</summary>
    public static bool UpdateAvailable(string? installedVersion, string? latestVersion) =>
        installedVersion is { Length: > 0 } && latestVersion is { Length: > 0 } && !string.Equals(installedVersion, latestVersion, StringComparison.Ordinal);

    public static SaveSyncStatus SaveStatus(bool supported, bool globalEnabled, GameEntry entry) =>
        !supported ? SaveSyncStatus.NotSupported
        : !globalEnabled || !entry.Settings.CloudSaves ? SaveSyncStatus.Disabled
        : SaveSyncStatus.Unknown;

    /// <summary>Entitlement grant date (catalog item, then namespace, earliest), else the catalog creation date.</summary>
    public static DateTimeOffset? AddedAt(CatalogItem item, LegendaryLibrary lib)
    {
        if (lib.Entitlements is { } ents)
        {
            var byItem = ents.Where(e => e.GrantDate is not null && string.Equals(e.CatalogItemId, item.CatalogItemId, StringComparison.Ordinal))
                .Select(e => e.GrantDate).Min();
            if (byItem is not null)
            {
                return byItem;
            }

            if (item.Namespace.Length > 0)
            {
                var byNs = ents.Where(e => e.GrantDate is not null && string.Equals(e.Namespace, item.Namespace, StringComparison.Ordinal))
                    .Select(e => e.GrantDate).Min();
                if (byNs is not null)
                {
                    return byNs;
                }
            }
        }

        return item.CreationDate;
    }

    public static GameArt ArtFor(CatalogItem item, CatalogContext ctx)
    {
        var art = Art(item, ctx.Placeholders);
        if (art.Tall is null && art.Wide is null && ctx.CachedArt?.GetValueOrDefault(item.AppName) is { } cached && (cached.Tall ?? cached.Wide) is not null)
        {
            return cached with { Placeholder = cached.Placeholder ?? art.Placeholder };
        }

        return art;
    }

    /// <summary>
    /// Tile = DieselGameBoxTall, hero = DieselGameBox, each falling back to the other. The URLs are the host's lazy art cache
    /// (<c>https://art.glider/…</c>, <see cref="ArtUrls"/>); the Epic CDN URL is resolved by <see cref="ArtSourceOf"/> on first use.
    /// </summary>
    public static GameArt Art(CatalogItem item, IReadOnlyDictionary<string, ArtPlaceholder>? placeholders = null)
    {
        string? Url(ArtKind kind) => ArtSourceOf(item, kind) is { } s ? ArtUrls.Build(item.AppName, kind, s.Url) : null;
        var thumbSource = ArtSourceOf(item, ArtKind.TallThumb);
        var learned = thumbSource is not null && placeholders is not null && placeholders.TryGetValue(item.AppName, out var p) && p.Hash == ArtUrls.HashOf(thumbSource.Url)
            ? p.Color
            : null;
        return new GameArt(Url(ArtKind.Tall), Url(ArtKind.TallThumb), Url(ArtKind.Wide), Url(ArtKind.Logo), learned ?? Placeholder(item.AppName));
    }

    /// <summary>The Epic picture behind a variant: tall ← wide and wide ← tall when one is missing; logo only if present.</summary>
    public static ArtSource? ArtSourceOf(CatalogItem item, ArtKind kind)
    {
        var tall = Pick(item.KeyImages, TallType);
        var wide = Pick(item.KeyImages, WideType);
        var picked = kind switch
        {
            ArtKind.Tall or ArtKind.TallThumb => tall ?? wide,
            ArtKind.Wide => wide ?? tall,
            _ => Pick(item.KeyImages, LogoType),
        };
        return picked is null ? null : new ArtSource(picked.Url, picked.Width);
    }

    private static KeyImage? Pick(IReadOnlyList<KeyImage> images, string type) => images
        .Where(i => string.Equals(i.Type, type, StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(i.Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
        .OrderByDescending(i => (long)(i.Width ?? 0) * (i.Height ?? 0))
        .FirstOrDefault();

    /// <summary>A stable, muted colour per app (CSS hex) for blur-up and art-less tiles.</summary>
    public static string Placeholder(string appName)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(appName));
        var hue = (h[0] << 8 | h[1]) % 360 / 360.0;
        var (r, g, b) = HslToRgb(hue, 0.35, 0.32);
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    private static (int R, int G, int B) HslToRgb(double h, double s, double l)
    {
        double Channel(double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }

        return ((int)Math.Round(Channel(h + 1.0 / 3) * 255), (int)Math.Round(Channel(h) * 255), (int)Math.Round(Channel(h - 1.0 / 3) * 255));
    }

    /// <summary>Upserts/removes turning <paramref name="from"/> into <paramref name="to"/> (record equality per game).</summary>
    public static (List<GameSummary> Upserts, List<string> Removes) Diff(
        IReadOnlyDictionary<string, GameSummary> from, IReadOnlyDictionary<string, GameSummary> to)
    {
        var upserts = new List<GameSummary>();
        var removes = new List<string>();
        foreach (var (app, s) in to)
        {
            if (!from.TryGetValue(app, out var old) || !Equals(old, s))
            {
                upserts.Add(s);
            }
        }

        foreach (var app in from.Keys)
        {
            if (!to.ContainsKey(app))
            {
                removes.Add(app);
            }
        }

        upserts.Sort((a, b) => string.CompareOrdinal(a.AppName, b.AppName));
        removes.Sort(StringComparer.Ordinal);
        return (upserts, removes);
    }
}
