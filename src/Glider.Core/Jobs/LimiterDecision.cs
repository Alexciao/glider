using System.Globalization;
using Glider.Core.Contracts;

namespace Glider.Core.Jobs;

/// <summary>What the limiter logic knows about the running download.</summary>
internal readonly record struct LimiterInput(
    JobPhase Phase,
    DownloadMode Mode,
    string? ModeReason,
    bool DiskBound,
    bool CdnSlow,
    long? SpeedLimitBytesPerSecond,
    double RawBytesPerSecond,
    double WriteBytesPerSecond,
    double PeakNetworkBytesPerSecond,
    double? ProbeMegabytesPerSecond,
    string? Drive,
    string? CdnHost,
    double SecondsInPhase);

/// <summary>
/// "What is limiting speed right now" plus a sentence the UI can show as is (docs/download-perf.md §5.7). Order matters: a user
/// speed limit or the Background mode explain a slow download before anything else does; then a drive that can't keep up (never
/// blame the CDN for a full cache), then a slow CDN; otherwise the connection by elimination, once there are enough seconds to tell.
/// </summary>
internal static class LimiterDecision
{
    public static (Limiter Limiter, string? Reason) Decide(in LimiterInput i)
    {
        var drive = i.Drive is { Length: > 0 } d ? $"drive {d}" : "your drive";
        switch (i.Phase)
        {
            case JobPhase.Verifying:
                return (Limiter.Disk, $"Reading the files on {drive}.");
            case JobPhase.Installing:
                return (Limiter.Disk, $"The download is done; Glider is still writing files to {drive}.");
            case JobPhase.Moving:
                return (Limiter.Disk, $"Copying files on {drive}.");
            case not (JobPhase.Downloading or JobPhase.Repairing):
                return (Limiter.Unknown, null);
        }

        if (i.SpeedLimitBytesPerSecond is { } limit && i.RawBytesPerSecond >= 0.85 * limit)
        {
            return (Limiter.SpeedLimit, $"Limited by your speed limit ({Mb(limit)}).");
        }

        if (i.Mode == DownloadMode.Background)
        {
            return (Limiter.Mode, i.ModeReason ?? "Background mode keeps the download out of your way.");
        }

        if (i.DiskBound)
        {
            var ceiling = Math.Max(i.PeakNetworkBytesPerSecond, (i.ProbeMegabytesPerSecond ?? 0) * 1_000_000);
            var write = i.WriteBytesPerSecond > 0 ? $" (≈{Mb(i.WriteBytesPerSecond)})" : "";
            var net = ceiling > 0 ? $" Your connection can do ≈{Mb(ceiling)}." : "";
            return (Limiter.Disk, $"Limited by {drive}{write}.{net} A nearly full or slow drive writes more slowly than your connection delivers.");
        }

        if (i.CdnSlow)
        {
            return (Limiter.Cdn, $"The download server{(i.CdnHost is { Length: > 0 } h ? $" ({h})" : "")} is slow right now; Glider will try another one.");
        }

        return i.SecondsInPhase < 8 ? (Limiter.Unknown, null) : (Limiter.Network, "Limited by your connection or Epic's servers.");
    }

    private static string Mb(double bytesPerSecond) => $"{(bytesPerSecond / 1_000_000).ToString(bytesPerSecond >= 10_000_000 ? "0" : "0.0", CultureInfo.InvariantCulture)} MB/s";
}
