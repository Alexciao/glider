using System.Text.Json;

namespace Glider.App;

// Pure (no WebView2/WPF types) so tests/Glider.Core.Tests can link this file.

/// <summary>
/// Makes the Epic login WebView2 present the same client hints as the installed Microsoft Edge. The only identity difference
/// found by <c>--login-audit</c> is the extra "Microsoft Edge WebView2" brand (and its position) in <c>sec-ch-ua</c> /
/// <c>navigator.userAgentData</c>; every value here is taken from the running runtime, nothing is invented.
/// </summary>
public static class EdgeUserAgentMetadata
{
    private const string EdgeBrand = "Microsoft Edge";
    private const string ChromiumBrand = "Chromium";

    // Chromium's brand-list shuffle: the seed is the major version, slot 0 = greased brand, 1 = Chromium, 2 = the vendor brand.
    private static readonly int[][] Orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];

    /// <param name="userAgent">The user agent the view already uses (already Edge-shaped, left unchanged).</param>
    /// <param name="highEntropyJson">JSON of <c>navigator.userAgentData.getHighEntropyValues([...])</c> from the same view.</param>
    /// <returns>Parameters for <c>Emulation.setUserAgentOverride</c>, or null when the runtime's data is not what we expect (then nothing is overridden).</returns>
    public static string? BuildOverrideJson(string userAgent, string highEntropyJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(highEntropyJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("fullVersionList", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? edge = null, chromium = null;
            (string Brand, string Version)? grease = null;
            foreach (var item in list.EnumerateArray())
            {
                var brand = item.GetProperty("brand").GetString() ?? "";
                var version = item.GetProperty("version").GetString() ?? "";
                if (brand.Contains("WebView2", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (brand == EdgeBrand)
                {
                    edge = version;
                }
                else if (brand == ChromiumBrand)
                {
                    chromium = version;
                }
                else
                {
                    grease = (brand, version);
                }
            }

            if (edge is null || chromium is null || grease is null || !int.TryParse(Major(edge), out var seed))
            {
                return null;
            }

            var slots = new (string Brand, string Version)[3];
            var order = Orders[seed % 6];
            slots[order[0]] = grease.Value;
            slots[order[1]] = (ChromiumBrand, chromium);
            slots[order[2]] = (EdgeBrand, edge);

            string Text(string name, string fallback) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
            var metadata = new Dictionary<string, object?>
            {
                ["brands"] = slots.Select(s => new { brand = s.Brand, version = Major(s.Version) }).ToArray(),
                ["fullVersionList"] = slots.Select(s => new { brand = s.Brand, version = s.Version }).ToArray(),
                ["platform"] = Text("platform", "Windows"),
                ["platformVersion"] = Text("platformVersion", ""),
                ["architecture"] = Text("architecture", "x86"),
                ["model"] = Text("model", ""),
                ["mobile"] = false,
                ["bitness"] = Text("bitness", "64"),
                ["wow64"] = root.TryGetProperty("wow64", out var w) && w.ValueKind == JsonValueKind.True,
            };
            return JsonSerializer.Serialize(new Dictionary<string, object?> { ["userAgent"] = userAgent, ["userAgentMetadata"] = metadata });
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Major(string version) => version.Split('.')[0];
}
