using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Glider.Core.Egl;

/// <summary>One file of an Epic build manifest.</summary>
public sealed record ManifestFile(string Name, long Size, IReadOnlyList<string> Tags);

/// <summary>What Glider needs from an Epic build manifest: the build and, per file, its size and install tags.</summary>
public sealed record EpicManifestSummary(string BuildVersion, string LaunchExecutable, IReadOnlyList<ManifestFile> Files)
{
    /// <summary>Every distinct non-empty install tag of the build.</summary>
    public IReadOnlyList<string> AllTags => Files.SelectMany(f => f.Tags).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    public bool HasTags => Files.Any(f => f.Tags.Count > 0);
}

/// <summary>
/// Minimal reader for Epic's binary build manifests (the <c>&lt;guid&gt;.manifest</c> in EGL's <c>.egstore</c> and Legendary's
/// <c>manifests\</c>): header, meta (build version, launch exe) and the file list with sizes and install tags. Layout from
/// Legendary's <c>models/manifest.py</c>. Reads only; returns null for anything it doesn't understand (JSON manifests,
/// encrypted ones, truncated files) so callers fall back to not knowing, never to guessing.
/// </summary>
public static class EpicManifestReader
{
    private const uint Magic = 0x44BEC00C;

    /// <summary>Real manifests are ~10 MB for a 1000-file game; refuse absurd inputs.</summary>
    private const long MaxManifestBytes = 256L * 1024 * 1024;

    private const int MaxFiles = 4_000_000;

    public static EpicManifestSummary? TryReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 41 or > MaxManifestBytes)
            {
                return null;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return TryRead(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static EpicManifestSummary? TryRead(byte[] data)
    {
        try
        {
            return Read(data);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IndexOutOfRangeException or OverflowException or DecoderFallbackException or EndOfStreamException)
        {
            return null;
        }
    }

    private static EpicManifestSummary? Read(byte[] raw)
    {
        if (raw.Length < 41 || BinaryPrimitives.ReadUInt32LittleEndian(raw) != Magic)
        {
            return null;
        }

        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
        var storedAs = raw[36];
        if ((storedAs & 2) != 0 || headerSize < 41 || headerSize > raw.Length)
        {
            return null; // encrypted (Legendary stores decrypted copies; EGL's may not be), or not a header we know
        }

        byte[] body;
        if ((storedAs & 1) != 0)
        {
            using var zlib = new ZLibStream(new MemoryStream(raw, headerSize, raw.Length - headerSize), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            zlib.CopyTo(ms);
            body = ms.ToArray();
        }
        else
        {
            body = raw.AsSpan(headerSize).ToArray();
        }

        var r = new Cursor(body);

        // --- meta: size, version, feature level, is_file_data, app_id, then the strings we want
        var metaStart = r.Position;
        var metaSize = (int)r.U32();
        r.Skip(1 + 4 + 1 + 4);
        _ = r.FString(); // the build's own app name (e.g. "FortniteReleaseBuilds"), not the catalog AppName
        var buildVersion = r.FString();
        var launchExe = r.FString();
        r.Seek(metaStart + metaSize);

        // --- chunk list (skipped as a block)
        var chunkStart = r.Position;
        r.Seek(chunkStart + (int)r.U32());

        // --- file list: size, version, count, then column-wise arrays
        r.Skip(4 + 1);
        var count = (int)r.U32();
        if (count < 0 || count > MaxFiles)
        {
            return null;
        }

        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = r.FString();
        }

        for (var i = 0; i < count; i++)
        {
            _ = r.FString(); // symlink target
        }

        r.Skip(20L * count); // SHA-1 per file
        r.Skip(count); // flags
        var tags = new IReadOnlyList<string>[count];
        for (var i = 0; i < count; i++)
        {
            var n = (int)r.U32();
            if (n is < 0 or > 4096)
            {
                return null;
            }

            var list = n == 0 ? [] : new string[n];
            for (var j = 0; j < n; j++)
            {
                list[j] = r.FString();
            }

            tags[i] = list;
        }

        var files = new ManifestFile[count];
        for (var i = 0; i < count; i++)
        {
            var parts = (int)r.U32();
            long total = 0;
            for (var j = 0; j < parts; j++)
            {
                var partStart = r.Position;
                var partSize = (int)r.U32(); // the struct's own size (28), including these four bytes
                if (partSize < 28)
                {
                    return null;
                }

                r.Skip(16 + 4); // chunk guid, offset in the chunk
                total += r.U32();
                r.Seek(partStart + partSize);
            }

            files[i] = new ManifestFile(names[i], total, tags[i]);
        }

        return new EpicManifestSummary(buildVersion, launchExe, files);
    }

    private sealed class Cursor(byte[] data)
    {
        public int Position { get; private set; }

        public void Seek(int position)
        {
            if (position < 0 || position > data.Length)
            {
                throw new InvalidDataException("offset out of range");
            }

            Position = position;
        }

        public void Skip(long count)
        {
            if (count < 0 || Position + count > data.Length)
            {
                throw new InvalidDataException("skip out of range");
            }

            Position += (int)count;
        }

        public byte U8()
        {
            if (Position >= data.Length)
            {
                throw new InvalidDataException("end of data");
            }

            return data[Position++];
        }

        public uint U32()
        {
            if (Position + 4 > data.Length)
            {
                throw new InvalidDataException("end of data");
            }

            var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position));
            Position += 4;
            return v;
        }

        /// <summary>Unreal FString: int32 length incl. the terminator; negative = UTF-16 (-length characters).</summary>
        public string FString()
        {
            if (Position + 4 > data.Length)
            {
                throw new InvalidDataException("end of data");
            }

            var n = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(Position));
            Position += 4;
            if (n == 0)
            {
                return "";
            }

            if (n < 0)
            {
                var chars = checked(-n);
                var bytes = checked(chars * 2);
                if (chars > 1 << 20 || Position + bytes > data.Length)
                {
                    throw new InvalidDataException("string out of range");
                }

                var s = Encoding.Unicode.GetString(data, Position, bytes - 2);
                Position += bytes;
                return s;
            }

            if (n > 1 << 20 || Position + n > data.Length)
            {
                throw new InvalidDataException("string out of range");
            }

            var text = Encoding.UTF8.GetString(data, Position, n - 1);
            Position += n;
            return text;
        }
    }
}
