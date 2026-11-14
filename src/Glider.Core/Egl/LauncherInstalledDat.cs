using System.Text;
using System.Text.Json;

namespace Glider.Core.Egl;

/// <summary>One entry of <c>UnrealEngineLauncher\LauncherInstalled.dat</c>'s <c>InstallationList</c>.</summary>
internal sealed record LauncherInstalledEntry(
    string? InstallLocation,
    string? NamespaceId,
    string? ItemId,
    string? ArtifactId,
    string? AppVersion,
    string? AppName,
    /// <summary>Byte range (in the UTF-8 view) of the InstallLocation string token, quotes included.</summary>
    Range? InstallLocationSpan,
    Range? AppVersionSpan);

/// <summary>
/// Reads and surgically patches EGL's <c>LauncherInstalled.dat</c> (JSON <c>{"InstallationList":[{InstallLocation,
/// NamespaceId, ItemId, ArtifactId, AppVersion, AppName}, …]}</c>, written by EGL with tab indentation). Patching only
/// replaces the bytes of the two string values we own, so EGL's formatting, key order, unknown keys, BOM/encoding and
/// line endings are preserved exactly.
/// </summary>
internal static class LauncherInstalledDat
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static IReadOnlyList<LauncherInstalledEntry> Parse(byte[] fileBytes) => Parse(EglJson.ToUtf8(fileBytes, out _).Span);

    /// <summary>Parses the UTF-8 view; throws <see cref="JsonException"/> on malformed JSON.</summary>
    public static IReadOnlyList<LauncherInstalledEntry> Parse(ReadOnlySpan<byte> utf8)
    {
        var result = new List<LauncherInstalledEntry>();
        var reader = new Utf8JsonReader(utf8, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("LauncherInstalled.dat root is not an object");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isList = reader.ValueTextEquals("InstallationList"u8);
            reader.Read();
            if (!isList || reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                continue;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                result.Add(ReadEntry(ref reader));
            }
        }

        return result;
    }

    private static LauncherInstalledEntry ReadEntry(ref Utf8JsonReader reader)
    {
        string? location = null, ns = null, item = null, artifact = null, version = null, app = null;
        Range? locationSpan = null, versionSpan = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();
                continue;
            }

            var value = reader.GetString();
            var start = (int)reader.TokenStartIndex;
            var span = new Range(start, start + reader.ValueSpan.Length + 2);
            switch (name)
            {
                case "InstallLocation": location = value; locationSpan = span; break;
                case "NamespaceId": ns = value; break;
                case "ItemId": item = value; break;
                case "ArtifactId": artifact = value; break;
                case "AppVersion": version = value; versionSpan = span; break;
                case "AppName": app = value; break;
            }
        }

        return new LauncherInstalledEntry(location, ns, item, artifact, version, app, locationSpan, versionSpan);
    }

    /// <summary>
    /// Returns new file bytes with the <paramref name="appName"/> entry at <paramref name="matchLocation"/> updated, or null when no entry
    /// matches or nothing would change. Unreal's JSON escaping is used for the new values, with non-ASCII as \uXXXX so
    /// the result is valid whatever encoding EGL assumes.
    /// </summary>
    public static byte[]? Patch(byte[] fileBytes, string appName, string matchLocation, string? newLocation, string? newVersion)
    {
        var utf8 = EglJson.ToUtf8(fileBytes, out var flavor);
        var entries = Parse(utf8.Span);
        var entry = entries.FirstOrDefault(e =>
            string.Equals(e.AppName, appName, StringComparison.OrdinalIgnoreCase) &&
            EglPath.Same(e.InstallLocation, matchLocation));
        if (entry is null)
        {
            return null;
        }

        var edits = new List<(Range Span, string Value)>();
        if (newLocation is not null && entry.InstallLocationSpan is { } ls && entry.InstallLocation != newLocation)
        {
            edits.Add((ls, newLocation));
        }

        if (newVersion is not null && entry.AppVersionSpan is { } vs && entry.AppVersion != newVersion)
        {
            edits.Add((vs, newVersion));
        }

        if (edits.Count == 0)
        {
            return null;
        }

        var src = utf8.Span;
        var output = new List<byte>(src.Length + 256);
        var pos = 0;
        foreach (var (span, value) in edits.OrderBy(e => e.Span.Start.Value))
        {
            output.AddRange(src[pos..span.Start.Value]);
            output.AddRange(Encoding.ASCII.GetBytes(EncodeUnrealString(value)));
            pos = span.End.Value;
        }

        output.AddRange(src[pos..]);
        return EglJson.FromUtf8(output.ToArray(), flavor);
    }

    /// <summary>Unreal FJsonWriter-compatible escaping (plus \uXXXX for non-ASCII), quotes included.</summary>
    internal static string EncodeUnrealString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
