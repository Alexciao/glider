using System.Globalization;
using System.Text.Json.Serialization;

namespace Glider.Core.Saves;

/// <summary>One file of a save folder. Path is relative with forward slashes; the mtime is UTC FILETIME ticks.</summary>
internal sealed record SaveFileEntry(string Path, long Size, long MtimeUtcTicks, string? Hash)
{
    public DateTimeOffset Mtime => DateTimeOffset.FromFileTime(MtimeUtcTicks).ToUniversalTime();
}

/// <summary>The (filtered) files of the local save folder, as scanned now.</summary>
internal sealed record LocalSaveSnapshot(IReadOnlyList<SaveFileEntry> Files)
{
    public static LocalSaveSnapshot Empty { get; } = new([]);

    public bool HasFiles => Files.Count > 0;

    public long TotalBytes => Files.Sum(f => f.Size);

    public DateTimeOffset? LatestMtime => Files.Count == 0 ? null : DateTimeOffset.FromFileTime(Files.Max(f => f.MtimeUtcTicks)).ToUniversalTime();
}

/// <summary>The cloud manifests `list-saves` showed for one game (names like <c>2026.11.14-10.00.00.manifest</c>).</summary>
internal sealed record CloudSaveSnapshot(IReadOnlyList<string> Manifests)
{
    public static CloudSaveSnapshot Empty { get; } = new([]);

    public bool HasSaves => Manifests.Count > 0;

    /// <summary>The newest manifest by the timestamp in its name (Legendary names a save after the local mtime it was made from).</summary>
    public DateTimeOffset? Head => Manifests.Select(CloudManifests.TryParse).Where(d => d is not null).Max();
}

internal static class CloudManifests
{
    public const string Format = "yyyy.MM.dd-HH.mm.ss";
    public const string Suffix = ".manifest";

    public static DateTimeOffset? TryParse(string name)
    {
        var stem = name.EndsWith(Suffix, StringComparison.Ordinal) ? name[..^Suffix.Length] : name;
        return DateTimeOffset.TryParseExact(stem, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    public static string NameFor(DateTimeOffset utc) => utc.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture) + Suffix;
}

/// <summary>What the two sides looked like the last time Glider saw them agree (state/saves.json).</summary>
internal sealed record SaveLedgerEntry(
    string SavePath,
    DateTimeOffset LastSyncedAt,
    IReadOnlyList<SaveFileEntry> Files,
    IReadOnlyList<string> Manifests,
    /// <summary>baseline | upload | download | resolve-local | resolve-cloud</summary>
    string LastAction);

/// <summary>state/saves.json.</summary>
internal sealed class SavesFile
{
    public int SchemaVersion { get; set; } = 2;

    public Dictionary<string, SaveLedgerEntry> Games { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(SavesFile))]
[JsonSerializable(typeof(SaveBackupInfo))]
internal sealed partial class SaveJsonContext : JsonSerializerContext;

internal enum SaveDecisionKind
{
    /// <summary>No save on either side.</summary>
    NoSave,
    InSync,
    Upload,
    Download,
    /// <summary>Both sides changed since the last sync (or, with no history, both exist and differ): the user decides.</summary>
    Conflict,
}

internal sealed record SaveDecision(SaveDecisionKind Kind, string Reason);

/// <summary>Why a backup was taken: what it protects decides how long it is kept.</summary>
internal static class BackupReasons
{
    /// <summary>The local save as it was before a download or "keep cloud" replaced it. The valuable kind.</summary>
    public const string Overwritten = "overwritten";

    /// <summary>A copy of what was uploaded.</summary>
    public const string Uploaded = "uploaded";
}

internal sealed record SaveBackupInfo(string Reason, DateTimeOffset CreatedAt, int FileCount, long Bytes, string SavePath);
