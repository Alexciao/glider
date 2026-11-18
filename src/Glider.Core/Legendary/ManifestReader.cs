using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Glider.Core.Legendary;

/// <summary>
/// The chunk list of an Epic binary manifest, kept columnar (a 100k-chunk game would otherwise be 100k path strings
/// just to pick eight). Only what the CDN probe needs: where the chunks are on a CDN and how big they are.
/// </summary>
internal sealed class ChunkTable
{
    private readonly byte[] _body;
    private readonly int _guids;
    private readonly int _hashes;
    private readonly int _groups;
    private readonly int _windows;
    private readonly int _sizes;

    public ChunkTable(byte[] body, int count, int featureLevel, int cdlOffset)
    {
        _body = body;
        Count = count;
        FeatureLevel = featureLevel;
        _guids = cdlOffset + 9;
        _hashes = _guids + 16 * count;
        var sha = _hashes + 8 * count;
        _groups = sha + 20 * count;
        _windows = _groups + count;
        _sizes = _windows + 4 * count;
    }

    public int Count { get; }

    public int FeatureLevel { get; }

    /// <summary>Directory under the CDN base URL (legendary/models/manifest.py <c>get_chunk_dir</c>).</summary>
    public string ChunkDirectory => FeatureLevel switch
    {
        >= 22 => "ChunksV5",
        >= 15 => "ChunksV4",
        >= 6 => "ChunksV3",
        >= 3 => "ChunksV2",
        _ => "Chunks",
    };

    public int Group(int i) => _body[_groups + i];

    public int WindowSize(int i) => (int)BinaryPrimitives.ReadUInt32LittleEndian(_body.AsSpan(_windows + 4 * i));

    /// <summary>Compressed size = bytes on the wire.</summary>
    public long CompressedSize(int i) => BinaryPrimitives.ReadInt64LittleEndian(_body.AsSpan(_sizes + 8 * i));

    /// <summary>Path relative to a CDN base URL: <c>ChunksV4/68/B17A61018DED91E8_02CF2C39…chunk</c>.</summary>
    public string Path(int i)
    {
        var hash = BinaryPrimitives.ReadUInt64LittleEndian(_body.AsSpan(_hashes + 8 * i));
        var g = _body.AsSpan(_guids + 16 * i, 16);
        return $"{ChunkDirectory}/{Group(i):D2}/{hash:X16}_{BinaryPrimitives.ReadUInt32LittleEndian(g):X8}" +
               $"{BinaryPrimitives.ReadUInt32LittleEndian(g[4..]):X8}{BinaryPrimitives.ReadUInt32LittleEndian(g[8..]):X8}{BinaryPrimitives.ReadUInt32LittleEndian(g[12..]):X8}.chunk";
    }

    /// <summary>
    /// Up to <paramref name="count"/> chunks worth timing (full 1 MiB windows, ≥ 900 KiB on the wire; small games fall back to
    /// anything ≥ 64 KiB), spread evenly over the list. <paramref name="seed"/> shifts the picks so two jobs don't fetch the same
    /// objects (a CDN edge that cached the last probe would otherwise look faster).
    /// </summary>
    public IReadOnlyList<string> PickProbeChunks(int count, int seed = 0)
    {
        var candidates = new List<int>();
        for (var i = 0; i < Count; i++)
        {
            if (WindowSize(i) == 1 << 20 && CompressedSize(i) >= 900 * 1024)
            {
                candidates.Add(i);
            }
        }

        if (candidates.Count < count)
        {
            candidates.Clear();
            for (var i = 0; i < Count; i++)
            {
                if (CompressedSize(i) >= 64 * 1024)
                {
                    candidates.Add(i);
                }
            }
        }

        var picked = new List<string>(Math.Min(count, candidates.Count));
        if (candidates.Count == 0 || count <= 0)
        {
            return picked;
        }

        var step = Math.Max(1, candidates.Count / count);
        var offset = candidates.Count > count ? (int)((uint)seed * 2654435761u % (uint)step) : 0;
        for (var k = 0; k < count && k * step + offset < candidates.Count; k++)
        {
            picked.Add(Path(candidates[k * step + offset]));
        }

        return picked;
    }
}

/// <summary>What <c>legendary verify</c> reads for an install: how many files and their summed size (cli.py:1281-1293).</summary>
internal readonly record struct ManifestVerifyTotal(int Files, long Bytes);

/// <summary>
/// Minimal reader for Epic's binary manifest (legendary/models/manifest.py): header (magic 0x44BEC00C), optional zlib body,
/// then the metadata block (skipped by its size field), the chunk data list and the file manifest list. Returns null for
/// anything it can't handle (for the chunk list: encrypted bodies and feature level ≥ 22, whose chunk URLs need per-chunk
/// secrets; for either: damaged files). Callers just do without (no CDN probe, no verify byte total).
/// </summary>
internal static class ManifestReader
{
    private const uint Magic = 0x44BEC00C;

    /// <summary>A chunk part's serialized size; Legendary's reader always advances by this, whatever the part's size field says.</summary>
    private const int ChunkPartSize = 28;

    public static ChunkTable? TryReadChunks(ReadOnlySpan<byte> file)
    {
        try
        {
            return ReadChunks(file);
        }
        catch (Exception e) when (IsDamage(e))
        {
            return null;
        }
    }

    /// <summary>
    /// The files <c>legendary verify</c> checks and their summed size, the total its "Verification progress" percent is a
    /// share of: every file when <paramref name="configTags"/> is null (no <c>[app] install_tags</c> in config.ini), else the
    /// files with one of those tags plus the untagged ones (cli.py:1281-1293). Null when the file list can't be read.
    /// </summary>
    /// <param name="configTags">The config.ini value as is (comma-separated; Legendary strips each item), or null.</param>
    public static ManifestVerifyTotal? TryReadVerifyTotal(ReadOnlySpan<byte> file, string? configTags)
    {
        try
        {
            return ReadVerifyTotal(file, configTags);
        }
        catch (Exception e) when (IsDamage(e))
        {
            return null;
        }
    }

    private static bool IsDamage(Exception e) =>
        e is InvalidDataException or IOException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or CryptographicException;

    private static ManifestVerifyTotal? ReadVerifyTotal(ReadOnlySpan<byte> file, string? configTags)
    {
        // Encrypted manifests (feature level 22+) encrypt only the file names and launch strings; sizes and tags stay plain.
        if (ReadBody(file, out _, out _) is not { } bytes)
        {
            return null;
        }

        ReadOnlySpan<byte> body = bytes;
        var metaSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
        var cdlSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[metaSize..]);
        var fml = checked(metaSize + cdlSize);
        // File manifest list: size u32, version u8, count u32, then columns: names, symlink targets, SHA-1s, flags, tags, parts.
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[(fml + 5)..]);
        if (count < 0 || count > body.Length)
        {
            return null;
        }

        var p = fml + 9;
        for (var i = 0; i < 2 * count; i++)
        {
            p = SkipFString(body, p);
        }

        p = checked(p + 21 * count);
        HashSet<string>? wanted = configTags is null ? null : [.. configTags.Split(',').Select(t => t.Trim())];
        var included = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var tags = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[p..]);
            p += 4;
            if (tags < 0 || tags > body.Length)
            {
                return null;
            }

            included[i] = wanted is null || tags == 0;
            for (var t = 0; t < tags; t++)
            {
                if (!included[i])
                {
                    included[i] = wanted!.Contains(ReadFString(body, p));
                }

                p = SkipFString(body, p);
            }
        }

        var files = 0;
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var parts = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[p..]);
            p += 4;
            if (parts < 0 || (long)parts * ChunkPartSize > body.Length - p)
            {
                return null;
            }

            long size = 0;
            for (var c = 0; c < parts; c++, p += ChunkPartSize)
            {
                size += BinaryPrimitives.ReadUInt32LittleEndian(body[(p + 24)..]); // struct size, GUID, offset, size
            }

            if (included[i])
            {
                files++;
                total += size;
            }
        }

        return new ManifestVerifyTotal(files, total);
    }

    /// <summary>An FString (manifest.py read_fstring): i32 length; positive = that many ASCII bytes incl. NUL, negative = UTF-16 chars incl. NUL.</summary>
    private static int SkipFString(ReadOnlySpan<byte> body, int p)
    {
        var n = BinaryPrimitives.ReadInt32LittleEndian(body[p..]);
        var end = p + 4 + (n < 0 ? -2L * n : n);
        if (end > body.Length)
        {
            throw new InvalidDataException("A manifest string runs past the end of the file.");
        }

        return (int)end;
    }

    private static string ReadFString(ReadOnlySpan<byte> body, int p)
    {
        var n = BinaryPrimitives.ReadInt32LittleEndian(body[p..]);
        var end = SkipFString(body, p);
        return n switch
        {
            > 0 => System.Text.Encoding.ASCII.GetString(body[(p + 4)..(end - 1)]),
            < 0 => System.Text.Encoding.Unicode.GetString(body[(p + 4)..(end - 2)]),
            _ => "",
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "The manifest format defines SHA-1 as its body checksum; it is an integrity check on a local file, not authentication.")]
    private static byte[]? ReadBody(ReadOnlySpan<byte> file, out byte storedAs, out uint version)
    {
        storedAs = 0;
        version = 0;
        if (file.Length < 41 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic)
        {
            return null;
        }

        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        var sizeUncompressed = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        var shaExpected = file.Slice(16, 20);
        storedAs = file[36];
        version = BinaryPrimitives.ReadUInt32LittleEndian(file[37..]);
        if (headerSize < 41 || headerSize > file.Length)
        {
            return null;
        }

        if ((storedAs & 1) == 0)
        {
            return file[headerSize..].ToArray();
        }

        if (sizeUncompressed < 0)
        {
            return null;
        }

        var body = new byte[sizeUncompressed];
        using var input = new MemoryStream(file[headerSize..].ToArray());
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        z.ReadExactly(body);
        return SHA1.HashData(body).AsSpan().SequenceEqual(shaExpected) ? body : null;
    }

    private static ChunkTable? ReadChunks(ReadOnlySpan<byte> file)
    {
        if (ReadBody(file, out var storedAs, out var version) is not { } body)
        {
            return null;
        }

        if ((storedAs & 0x2) != 0 || version >= 22)
        {
            return null; // encrypted, or ChunksV5 (secrets): chunk URLs can't be fetched without them
        }

        // Metadata block: meta_size u32 (whole block incl. itself), data_version u8, feature_level u32, …
        var metaSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
        var featureLevel = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5));
        var cdl = metaSize;
        // Chunk data list: size u32, version u8, count u32, then six columns.
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(cdl + 5));
        var needed = 9L + count * (16L + 8 + 20 + 1 + 4 + 8);
        if (count < 0 || cdl + needed > body.Length)
        {
            return null;
        }

        return new ChunkTable(body, count, featureLevel, cdl);
    }
}
