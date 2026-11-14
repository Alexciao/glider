using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Glider.Core.Egl;

namespace Glider.Core.Tests;

/// <summary>Writes Epic binary build manifests (the layout <see cref="EpicManifestReader"/> reads) for tests.</summary>
internal static class ManifestBuilder
{
    public static byte[] Build(string buildVersion, IReadOnlyList<ManifestFile> files, bool compressed = true, bool encrypted = false, string launchExe = "Game.exe")
    {
        var body = new MemoryStream();
        // meta: size, version, feature level, is_file_data, app_id, app_name, build_version, launch_exe, launch_command, then padding
        var meta = new MemoryStream();
        U8(meta, 1);
        U32(meta, 18);
        U8(meta, 0);
        U32(meta, 0);
        FString(meta, "TestBuilds");
        FString(meta, buildVersion);
        FString(meta, launchExe);
        FString(meta, "");
        U32(body, (uint)(meta.Length + 4));
        body.Write(meta.ToArray());

        // chunk list: empty (size, version, count)
        U32(body, 9);
        U8(body, 0);
        U32(body, 0);

        // file list
        var list = new MemoryStream();
        U8(list, 0);
        U32(list, (uint)files.Count);
        foreach (var f in files)
        {
            FString(list, f.Name);
        }

        foreach (var _ in files)
        {
            FString(list, "");
        }

        foreach (var _ in files)
        {
            list.Write(new byte[20]);
        }

        foreach (var _ in files)
        {
            U8(list, 0);
        }

        foreach (var f in files)
        {
            U32(list, (uint)f.Tags.Count);
            foreach (var t in f.Tags)
            {
                FString(list, t);
            }
        }

        foreach (var f in files)
        {
            U32(list, 1); // one chunk part
            U32(list, 28);
            list.Write(new byte[16]);
            U32(list, 0);
            U32(list, (uint)f.Size);
        }

        U32(body, (uint)(list.Length + 4));
        body.Write(list.ToArray());

        var raw = body.ToArray();
        var payload = raw;
        if (compressed)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(raw);
            }

            payload = ms.ToArray();
        }

        var header = new byte[41];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x44BEC00C);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 41);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)raw.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)payload.Length);
        header[36] = (byte)((compressed ? 1 : 0) | (encrypted ? 2 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(37), 18);
        return [.. header, .. payload];
    }

    public static ManifestFile F(string name, long size = 10, params string[] tags) => new(name, size, tags);

    /// <summary>Creates the files of <paramref name="files"/> under <paramref name="root"/> (each with its listed size... as 1 byte).</summary>
    public static void Touch(string root, params string[] names)
    {
        foreach (var n in names)
        {
            var p = Path.Combine(root, n.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");
        }
    }

    private static void U8(Stream s, byte v) => s.WriteByte(v);

    private static void U32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void FString(Stream s, string v)
    {
        if (v.Length == 0)
        {
            U32(s, 0);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(v);
        U32(s, (uint)(bytes.Length + 1));
        s.Write(bytes);
        s.WriteByte(0);
    }
}
