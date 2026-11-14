using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glider.Core.Links;

public enum RegKind
{
    String,
    ExpandString,
    DWord,
    QWord,
    Binary,
    MultiString,
}

/// <summary>One registry value, typed so a restore puts back exactly what was there.</summary>
public sealed class RegValue
{
    public RegKind Kind { get; set; }

    public string? Text { get; set; }

    public long Number { get; set; }

    public byte[]? Bytes { get; set; }

    public string[]? Multi { get; set; }

    public static RegValue Str(string text) => new() { Kind = RegKind.String, Text = text };

    public bool SameAs(RegValue? other) =>
        other is not null && Kind == other.Kind && Text == other.Text && Number == other.Number
        && (Bytes ?? []).AsSpan().SequenceEqual(other.Bytes ?? [])
        && (Multi ?? []).AsSpan().SequenceEqual(other.Multi ?? []);
}

/// <summary>A registry key and everything under it. Value and key names are case-insensitive (like the registry); the default value is "".</summary>
public sealed class RegistryNode
{
    public Dictionary<string, RegValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, RegistryNode> Keys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public RegistryNode Set(string name, string text)
    {
        Values[name] = RegValue.Str(text);
        return this;
    }

    public RegistryNode Child(string name)
    {
        if (!Keys.TryGetValue(name, out var node))
        {
            Keys[name] = node = new RegistryNode();
        }

        return node;
    }

    public string? GetString(string name = "") => Values.TryGetValue(name, out var v) && v.Kind is RegKind.String or RegKind.ExpandString ? v.Text : null;

    public RegistryNode? Find(params string[] path)
    {
        var node = this;
        foreach (var part in path)
        {
            if (!node.Keys.TryGetValue(part, out var next))
            {
                return null;
            }

            node = next;
        }

        return node;
    }

    /// <summary>Deep, case-insensitive, type-aware comparison.</summary>
    public bool SameAs(RegistryNode? other)
    {
        if (other is null || Values.Count != other.Values.Count || Keys.Count != other.Keys.Count)
        {
            return false;
        }

        foreach (var (name, value) in Values)
        {
            if (!other.Values.TryGetValue(name, out var o) || !value.SameAs(o))
            {
                return false;
            }
        }

        foreach (var (name, child) in Keys)
        {
            if (!other.Keys.TryGetValue(name, out var o) || !child.SameAs(o))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Deserialized dictionaries lose their comparer; rebuild them so lookups stay case-insensitive.</summary>
    internal RegistryNode Normalized()
    {
        var copy = new RegistryNode();
        foreach (var (name, value) in Values ?? [])
        {
            copy.Values[name] = value;
        }

        foreach (var (name, child) in Keys ?? [])
        {
            copy.Keys[name] = child.Normalized();
        }

        return copy;
    }
}

/// <summary>
/// The slice of the registry Glider touches, always under HKEY_CURRENT_USER (paths are relative to it, e.g. <c>Software\Classes\glider</c>).
/// The real implementation lives in Glider.App; tests use an in-memory tree so no test ever writes the machine's registry.
/// </summary>
public interface IRegistryAccess
{
    /// <summary>The key with all values and subkeys, or null when it does not exist.</summary>
    RegistryNode? Read(string keyPath);

    /// <summary>Deletes the key (if any) and everything under it, then writes <paramref name="node"/> in its place.</summary>
    void Replace(string keyPath, RegistryNode node);

    /// <summary>Deletes the key and everything under it; no error when it does not exist.</summary>
    void Delete(string keyPath);
}

/// <summary>Written before Glider takes a protocol over, read back to restore it.</summary>
public sealed class ProtocolBackup
{
    public int SchemaVersion { get; set; } = 1;

    public string Scheme { get; set; } = "";

    public DateTimeOffset SavedAt { get; set; }

    /// <summary>False: the user had no HKCU entry for the scheme (any handler came from the machine-wide registration), so restoring means deleting ours.</summary>
    public bool HkcuExisted { get; set; }

    public RegistryNode? Tree { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ProtocolBackup))]
internal sealed partial class LinksJsonContext : JsonSerializerContext;

internal static class ProtocolBackupFile
{
    public static void Save(string path, ProtocolBackup backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(backup, LinksJsonContext.Default.ProtocolBackup));
        File.Move(temp, path, overwrite: true);
    }

    public static ProtocolBackup? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var backup = JsonSerializer.Deserialize(File.ReadAllText(path), LinksJsonContext.Default.ProtocolBackup);
            if (backup is null)
            {
                return null;
            }

            backup.Tree = backup.Tree?.Normalized();
            return backup;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
