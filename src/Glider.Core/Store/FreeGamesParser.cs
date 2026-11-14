using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glider.Core.Contracts;

namespace Glider.Core.Store;

/// <summary>One offer of the feed that is (or will be) free, before it is mapped onto the UI contract.</summary>
public sealed record FreeOffer(
    string OfferId,
    string Namespace,
    IReadOnlyList<string> ItemIds,
    string Title,
    string OfferType,
    string? Seller,
    FreeGameStatus Status,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    /// <summary>Original (unresized) https art URL on epicgames.com, or null.</summary>
    string? WideImage,
    string? TallImage,
    string? RegularPrice,
    string? StoreSlug,
    bool IsBundle);

/// <summary>Result of parsing the feed at one instant. <see cref="Boundaries"/> are the 0 % window edges that matter for re-evaluation.</summary>
public sealed record FreeGamesParse(IReadOnlyList<FreeOffer> Now, IReadOnlyList<FreeOffer> Soon, IReadOnlyList<DateTimeOffset> Boundaries)
{
    public static FreeGamesParse Empty { get; } = new([], [], []);
}

/// <summary>
/// Pure parser for Epic's public <c>freeGamesPromotions</c> feed (docs/research/store-webview.md §5.2-5.3). Everything in the
/// feed is untrusted: it never throws, skips elements it cannot understand, and only lets https epicgames.com image URLs and
/// validated slugs through.
/// </summary>
public static partial class FreeGamesParser
{
    /// <summary>Resize parameters the Epic CDN honours; keeps a 1.6 MB PNG down to a ~50 KB JPEG.</summary>
    public const string WideResize = "?h=270&resize=1&w=480";

    public const string TallResize = "?h=480&resize=1&w=360";

    public static FreeGamesParse Parse(JsonElement root, DateTimeOffset now)
    {
        try
        {
            if (!TryPath(root, out var elements, "data", "Catalog", "searchStore", "elements") || elements.ValueKind != JsonValueKind.Array)
            {
                return FreeGamesParse.Empty;
            }

            List<FreeOffer> nowList = [], soonList = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            HashSet<DateTimeOffset> boundaries = [];
            foreach (var el in elements.EnumerateArray())
            {
                FreeOffer? offer;
                try
                {
                    offer = ParseElement(el, now, boundaries);
                }
                catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
                {
                    continue; // one broken element never costs the others
                }

                if (offer is null || !seen.Add(offer.OfferId))
                {
                    continue;
                }

                (offer.Status == FreeGameStatus.FreeNow ? nowList : soonList).Add(offer);
            }

            return new FreeGamesParse(
                nowList.OrderBy(o => o.EndsAt).ThenBy(o => o.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                soonList.OrderBy(o => o.StartsAt).ThenBy(o => o.Title, StringComparer.OrdinalIgnoreCase).ToList(),
                boundaries.Order().ToList());
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            return FreeGamesParse.Empty;
        }
    }

    /// <summary>Parse from raw JSON text; empty on malformed input.</summary>
    public static FreeGamesParse Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Parse(doc.RootElement, now);
        }
        catch (JsonException)
        {
            return FreeGamesParse.Empty;
        }
    }

    private static FreeOffer? ParseElement(JsonElement el, DateTimeOffset now, HashSet<DateTimeOffset> boundaries)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = Str(el, "id");
        var title = Str(el, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        if (Str(el, "status") is { } status && !status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (el.TryGetProperty("isCodeRedemptionOnly", out var cro) && cro.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        // Windows: every 0 % window, active or upcoming (the feed is cached, so "upcoming" may have started since).
        FreeWindow? active = null, next = null;
        if (el.TryGetProperty("promotions", out var promos) && promos.ValueKind == JsonValueKind.Object)
        {
            foreach (var groupName in new[] { "promotionalOffers", "upcomingPromotionalOffers" })
            {
                foreach (var w in Windows(promos, groupName))
                {
                    if (w.End <= now)
                    {
                        continue;
                    }

                    boundaries.Add(w.Start);
                    boundaries.Add(w.End);
                    if (w.Start <= now)
                    {
                        active = active is null || w.End < active.Value.End ? w : active;
                    }
                    else
                    {
                        next = next is null || w.Start < next.Value.Start ? w : next;
                    }
                }
            }
        }

        var window = active ?? next;
        if (window is null)
        {
            return null;
        }

        var offerType = Str(el, "offerType") ?? "";
        var itemIds = new List<string>();
        if (el.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (Str(item, "id") is { Length: > 0 } iid)
                {
                    itemIds.Add(iid);
                }
            }
        }

        return new FreeOffer(
            id!,
            Str(el, "namespace") ?? "",
            itemIds,
            Clip(title!, 160),
            offerType,
            TryPath(el, out var seller, "seller", "name") ? Clip(seller.GetString() ?? "", 120) : null,
            active is not null ? FreeGameStatus.FreeNow : FreeGameStatus.Upcoming,
            window.Value.Start,
            window.Value.End,
            PickImage(el, "OfferImageWide", "DieselStoreFrontWide", "Thumbnail"),
            PickImage(el, "OfferImageTall", "DieselStoreFrontTall", "Thumbnail"),
            RegularPrice(el),
            ResolveSlug(el),
            offerType.Equals("BUNDLE", StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct FreeWindow(DateTimeOffset Start, DateTimeOffset End);

    private static IEnumerable<FreeWindow> Windows(JsonElement promos, string groupName)
    {
        if (!promos.TryGetProperty(groupName, out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("promotionalOffers", out var offers) || offers.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var o in offers.EnumerateArray())
            {
                // discountPercentage is the share still PAID: 0 = 100 % off. 20/35/55/70 are plain discounts, not giveaways.
                if (!TryPath(o, out var pct, "discountSetting", "discountPercentage")
                    || pct.ValueKind != JsonValueKind.Number || !pct.TryGetDouble(out var p) || p != 0)
                {
                    continue;
                }

                if (Date(o, "startDate") is { } start && Date(o, "endDate") is { } end && end > start)
                {
                    yield return new FreeWindow(start, end);
                }
            }
        }
    }

    // ------------------------------------------------------------------ slug / images / price

    /// <summary>catalogNs productHome → offerMappings productHome → productSlug (minus "/home") → urlSlug unless it is a raw id.</summary>
    internal static string? ResolveSlug(JsonElement el)
    {
        foreach (var arrayPath in new[] { new[] { "catalogNs", "mappings" }, new[] { "offerMappings" } })
        {
            if (TryPath(el, out var arr, arrayPath) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    if (Str(m, "pageType") == "productHome" && StoreSlug.TryClean(Str(m, "pageSlug"), out var s))
                    {
                        return s;
                    }
                }
            }
        }

        if (StoreSlug.TryClean(Str(el, "productSlug"), out var p))
        {
            return p;
        }

        return StoreSlug.TryClean(Str(el, "urlSlug"), out var u) ? u : null;
    }

    private static string? PickImage(JsonElement el, params string[] preference)
    {
        if (!el.TryGetProperty("keyImages", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var type in preference)
        {
            foreach (var img in images.EnumerateArray())
            {
                if (Str(img, "type") == type && SafeImageUrl(Str(img, "url")) is { } url)
                {
                    return url;
                }
            }
        }

        return null;
    }

    /// <summary>https only, on epicgames.com, no userinfo/port, existing query and fragment dropped. Null for anything else.</summary>
    public static string? SafeImageUrl(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)
            || u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length != 0 || !u.IsDefaultPort
            || !(u.Host.Equals("epicgames.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".epicgames.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return $"https://{u.Host}{u.AbsolutePath}";
    }

    private static string? RegularPrice(JsonElement el)
    {
        if (!TryPath(el, out var total, "price", "totalPrice"))
        {
            return null;
        }

        if (total.TryGetProperty("originalPrice", out var minor) && minor.ValueKind == JsonValueKind.Number && minor.TryGetInt64(out var cents) && cents <= 0)
        {
            return null; // nothing to cross out
        }

        return TryPath(total, out var fmt, "fmtPrice", "originalPrice") && fmt.GetString() is { Length: > 0 } s && s != "0" ? Clip(s, 24) : null;
    }

    // ------------------------------------------------------------------ JSON helpers (all total)

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement el, string name) =>
        Str(el, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;

    private static bool TryPath(JsonElement el, out JsonElement found, params string[] path)
    {
        found = el;
        foreach (var segment in path)
        {
            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(segment, out found))
            {
                return false;
            }
        }

        return true;
    }

    private static string Clip(string s, int max)
    {
        s = ControlChars().Replace(s, " ").Trim();
        return s.Length <= max ? s : s[..max];
    }

    [GeneratedRegex(@"[\p{C}]+")]
    private static partial Regex ControlChars();
}
