using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Glider.Core.Contracts;

namespace Glider.Core.Services;

/// <summary>
/// Is this volume an SSD? Asks the storage stack whether the device incurs a seek penalty (IOCTL_STORAGE_QUERY_PROPERTY,
/// StorageDeviceSeekPenaltyProperty): none = SSD/NVMe, penalty = hard disk. Needs no privilege (the volume is opened with no access
/// rights). Null when unknown (network, virtual or unreadable volumes).
/// </summary>
internal static class DriveMedia
{
    public static bool? IsSsd(string root)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
        {
            return null;
        }

        var handle = CreateFile($@"\\.\{root[..2]}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var query = new StoragePropertyQuery { PropertyId = 7, QueryType = 0 }; // StorageDeviceSeekPenaltyProperty, PropertyStandardQuery
            var result = new SeekPenaltyDescriptor();
            return DeviceIoControl(handle, 0x002D1400, ref query, (uint)Marshal.SizeOf<StoragePropertyQuery>(), // IOCTL_STORAGE_QUERY_PROPERTY
                ref result, (uint)Marshal.SizeOf<SeekPenaltyDescriptor>(), out _, IntPtr.Zero)
                ? !result.IncursSeekPenalty
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, ref StoragePropertyQuery input, uint inputSize, ref SeekPenaltyDescriptor output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Sustained write speed Glider actually saw on a drive the last time a download was disk-bound (<c>cache/drive-speeds.json</c>,
/// safe to delete). It is the best predictor of "this drive is slow in practice": a DRAM-less SATA SSD that is 81 % full wrote
/// 3-9 MB/s here while its datasheet says 500.
/// </summary>
internal static class DriveSpeeds
{
    private static readonly Lock Gate = new();

    public static string PathFor(AppPaths paths) => System.IO.Path.Combine(paths.Cache, "drive-speeds.json");

    public static string KeyOf(string driveRoot) => driveRoot.TrimEnd('\\', '/').ToUpperInvariant();

    public static IReadOnlyDictionary<string, double> Read(AppPaths paths)
    {
        try
        {
            lock (Gate)
            {
                var file = PathFor(paths);
                return File.Exists(file)
                    ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(file)) ?? []
                    : new Dictionary<string, double>();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, double>();
        }
    }

    /// <summary>Remembers <paramref name="bytesPerSecond"/> for the drive (averaged with the previous value, so one odd job doesn't rule).</summary>
    public static void Record(AppPaths paths, string driveRoot, double bytesPerSecond)
    {
        if (bytesPerSecond <= 0 || string.IsNullOrEmpty(driveRoot))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var all = new Dictionary<string, double>(Read(paths));
                var key = KeyOf(driveRoot);
                all[key] = all.TryGetValue(key, out var old) ? (old + bytesPerSecond) / 2 : bytesPerSecond;
                Directory.CreateDirectory(paths.Cache);
                File.WriteAllText(PathFor(paths), JsonSerializer.Serialize(all));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache; losing a sample is fine.
        }
    }
}

/// <summary>Plain-language warnings for the install-location picker (docs/download-perf.md 5.7): slow or nearly full target drives.</summary>
public static class DriveHints
{
    /// <summary>Below this a drive is "slow in practice": it can't keep up with an ordinary connection (about 25 MB/s).</summary>
    public const double SlowBytesPerSecond = 30e6;

    public static string? For(bool? isSsd, long freeBytes, long totalBytes, double? observedWriteBytesPerSecond, string root)
    {
        var drive = root.TrimEnd('\\', '/');
        if (observedWriteBytesPerSecond is > 0 and < SlowBytesPerSecond)
        {
            return $"Slow in practice: {drive} only wrote about {(observedWriteBytesPerSecond.Value / 1e6).ToString("0.#", CultureInfo.InvariantCulture)} MB/s during a recent download. " +
                   "Free up space on it or install on a faster drive.";
        }

        var used = totalBytes > 0 ? 100.0 * (totalBytes - freeBytes) / totalBytes : 0;
        if (isSsd == false)
        {
            return $"{drive} is a hard disk drive: installs and updates will be slow. An SSD is much faster.";
        }

        if (used >= 85)
        {
            return $"{drive} is {used:0}% full. Drives slow down a lot when nearly full: free up some space first.";
        }

        return null;
    }

    internal static IReadOnlyList<DriveInfoDto> Describe(IReadOnlyList<FixedDrive> drives, IReadOnlyDictionary<string, double> observed) =>
        [.. drives.Select(d =>
        {
            observed.TryGetValue(DriveSpeeds.KeyOf(d.RootPath), out var rate);
            return new DriveInfoDto(d.RootPath, null, d.FreeBytes, d.TotalBytes, d.IsSsd ?? false, For(d.IsSsd, d.FreeBytes, d.TotalBytes, rate > 0 ? rate : null, d.RootPath));
        })];
}
