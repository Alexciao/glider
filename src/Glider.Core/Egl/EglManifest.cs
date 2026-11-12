using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Glider.Core.Egl;

/// <summary>
/// Literal port of Legendary 0.21.1 <c>legendary/models/egl.py</c> <c>EGLManifest</c>: <see cref="FromJson"/> pops the
/// known keys (with Legendary's defaults) and keeps everything else as <see cref="Remainder"/>; <see cref="ToJson"/>
/// writes <c>_template</c>, then the remainder, then the known keys — the exact merge order of <c>to_json</c>.
/// Values are Python-equivalent objects (see <see cref="PyJson"/>).
/// </summary>
internal sealed class EglManifest
{
    // models/egl.py `_template` (order irrelevant: the writer sorts keys).
    private static Dictionary<string, object?> Template() => new(StringComparer.Ordinal)
    {
        ["AppCategories"] = new List<object?> { "public", "games", "applications" },
        ["AppName"] = "",
        ["AppVersionString"] = "",
        ["BaseURLs"] = new List<object?>(),
        ["BuildLabel"] = "",
        ["CatalogItemId"] = "",
        ["CatalogNamespace"] = "",
        ["ChunkDbs"] = new List<object?>(),
        ["CompatibleApps"] = new List<object?>(),
        ["DisplayName"] = "",
        ["FormatVersion"] = 0L,
        ["FullAppName"] = "",
        ["HostInstallationGuid"] = "",
        ["InstallComponents"] = new List<object?>(),
        ["InstallLocation"] = "",
        ["InstallSessionId"] = "",
        ["InstallSize"] = 0L,
        ["InstallTags"] = new List<object?>(),
        ["InstallationGuid"] = "",
        ["LaunchCommand"] = "",
        ["LaunchExecutable"] = "",
        ["MainGameAppName"] = "",
        ["MainWindowProcessName"] = "",
        ["MandatoryAppFolderName"] = "",
        ["ManifestLocation"] = "",
        ["OwnershipToken"] = "",
        ["PrereqIds"] = new List<object?>(),
        ["ProcessNames"] = new List<object?>(),
        ["StagingLocation"] = "",
        ["TechnicalType"] = "",
        ["VaultThumbnailUrl"] = "",
        ["VaultTitleText"] = "",
        ["bCanRunOffline"] = true,
        ["bIsApplication"] = true,
        ["bIsExecutable"] = true,
        ["bIsIncompleteInstall"] = false,
        ["bIsManaged"] = false,
        ["bNeedsValidation"] = false,
        ["bRequiresAuth"] = true,
    };

    // models/egl.py `from_json` pops, with their defaults. AppName has no default in Legendary (KeyError).
    private static readonly (string Key, Func<object?> Default)[] Known =
    [
        ("AppName", () => null),
        ("AppVersionString", () => null),
        ("BaseURLs", () => new List<object?>()),
        ("BuildLabel", () => ""),
        ("CatalogItemId", () => ""),
        ("CatalogNamespace", () => ""),
        ("DisplayName", () => ""),
        ("InstallLocation", () => ""),
        ("InstallSize", () => 0L),
        ("InstallTags", () => new List<object?>()),
        ("InstallationGuid", () => ""),
        ("LaunchCommand", () => ""),
        ("LaunchExecutable", () => ""),
        ("MainGameAppName", () => ""),
        ("MandatoryAppFolderName", () => ""),
        ("ManifestLocation", () => ""),
        ("OwnershipToken", () => "False"),
        ("StagingLocation", () => ""),
        ("bCanRunOffline", () => true),
        ("bIsIncompleteInstall", () => false),
        ("bNeedsValidation", () => false),
    ];

    private EglManifest(Dictionary<string, object?> values, Dictionary<string, object?> remainder)
    {
        Values = values;
        Remainder = remainder;
    }

    /// <summary>The keys Legendary models explicitly (always written by <see cref="ToJson"/>).</summary>
    public Dictionary<string, object?> Values { get; }

    /// <summary>Every other key from the source file, written back untouched.</summary>
    public Dictionary<string, object?> Remainder { get; }

    public static EglManifest Empty() => FromJson(new Dictionary<string, object?>(StringComparer.Ordinal));

    public static EglManifest FromJson(IDictionary<string, object?> json)
    {
        var copy = new Dictionary<string, object?>(json, StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, def) in Known)
        {
            values[key] = copy.Remove(key, out var v) ? v : def();
        }

        // tmp.ownership_token = strtobool(json.pop('OwnershipToken', 'False'))
        values["OwnershipToken"] = StrToBool(values["OwnershipToken"]);
        return new EglManifest(values, copy);
    }

    public Dictionary<string, object?> ToJson()
    {
        var output = Template();
        foreach (var (k, v) in Remainder)
        {
            output[k] = v;
        }

        foreach (var (k, v) in Values)
        {
            // out['OwnershipToken'] = str(self.ownership_token).lower()
            output[k] = k == "OwnershipToken" ? PyStrLower(v) : v;
        }

        return output;
    }

    // ---------------- typed, tolerant views (never throw on odd types) ----------------

    public string? AppName => AsString(Values["AppName"]);
    public string? AppVersionString => AsString(Values["AppVersionString"]);
    public string InstallLocation => AsString(Values["InstallLocation"]) ?? "";
    public string InstallationGuid => AsString(Values["InstallationGuid"]) ?? "";
    public string MainGameAppName => AsString(Values["MainGameAppName"]) ?? "";
    public long InstallSize => AsLong(Values["InstallSize"]) ?? 0;
    public bool IsIncompleteInstall => AsBool(Values["bIsIncompleteInstall"]) ?? false;
    public bool NeedsValidation => AsBool(Values["bNeedsValidation"]) ?? false;
    public bool OwnershipToken => AsBool(Values["OwnershipToken"]) ?? false;
    public string? CatalogItemId => AsString(Values["CatalogItemId"]);
    public string? CatalogNamespace => AsString(Values["CatalogNamespace"]);

    public string? GetString(string key) =>
        AsString(Values.TryGetValue(key, out var v) ? v : Remainder.GetValueOrDefault(key));

    public static string? AsString(object? v) => v as string;

    public static long? AsLong(object? v) => v switch
    {
        long l => l,
        int i => i,
        double d when d is >= long.MinValue and <= long.MaxValue => (long)d,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    public static bool? AsBool(object? v) => v switch
    {
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s => StrToBool(s) is long r ? r != 0 : null,
        _ => null,
    };

    /// <summary>
    /// <c>legendary.utils.cli.strtobool</c>: returns Python int 1/0 for strings. Legendary would crash on a non-string
    /// (or unknown string); we tolerate by passing the value through unchanged.
    /// </summary>
    private static object? StrToBool(object? v)
    {
        if (v is not string s)
        {
            return v;
        }

        return s.ToLowerInvariant() switch
        {
            "y" or "yes" or "t" or "true" or "on" or "1" => 1L,
            "n" or "no" or "f" or "false" or "off" or "0" or "" => 0L,
            _ => v,
        };
    }

    /// <summary>Python <c>str(x).lower()</c> for the value types that can occur.</summary>
    private static string PyStrLower(object? v) => v switch
    {
        null => "none",
        bool b => b ? "true" : "false",
        string s => s.ToLowerInvariant(),
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => PyJson.FloatRepr(d).ToLowerInvariant(),
        _ => (v.ToString() ?? "").ToLowerInvariant(),
    };
}

/// <summary>Tolerant decoding of EGL's JSON files (BOM, UTF-16, comments, trailing commas).</summary>
internal static class EglJson
{
    /// <summary>Returns the bytes as UTF-8 without BOM (EGL/Unreal may write UTF-16LE with BOM for non-ANSI text).</summary>
    public static ReadOnlyMemory<byte> ToUtf8(byte[] bytes, out TextFlavor flavor)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            flavor = TextFlavor.Utf8Bom;
            return bytes.AsMemory(3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            flavor = TextFlavor.Utf16LeBom;
            return Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            flavor = TextFlavor.Utf16BeBom;
            return Encoding.UTF8.GetBytes(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));
        }

        flavor = TextFlavor.Utf8;
        return bytes;
    }

    /// <summary>Re-encodes UTF-8 text into the file's original flavor (preserving its BOM).</summary>
    public static byte[] FromUtf8(ReadOnlySpan<byte> utf8, TextFlavor flavor)
    {
        switch (flavor)
        {
            case TextFlavor.Utf8Bom:
                var withBom = new byte[utf8.Length + 3];
                withBom[0] = 0xEF;
                withBom[1] = 0xBB;
                withBom[2] = 0xBF;
                utf8.CopyTo(withBom.AsSpan(3));
                return withBom;
            case TextFlavor.Utf16LeBom:
                return [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(utf8))];
            case TextFlavor.Utf16BeBom:
                return [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Encoding.UTF8.GetString(utf8))];
            default:
                return utf8.ToArray();
        }
    }

    /// <summary>Parses a JSON object tolerantly into the Python-equivalent model; null if not a JSON object.</summary>
    public static Dictionary<string, object?>? TryParseObject(byte[] bytes, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(ToUtf8(bytes, out _), PyJson.TolerantOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"root is {doc.RootElement.ValueKind}, not an object";
                return null;
            }

            error = null;
            return (Dictionary<string, object?>)PyJson.FromElement(doc.RootElement)!;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or DecoderFallbackException)
        {
            error = ex.Message;
            return null;
        }
    }
}

internal enum TextFlavor { Utf8, Utf8Bom, Utf16LeBom, Utf16BeBom }
