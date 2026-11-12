using System.Globalization;

namespace Glider.Core.Legendary;

internal enum ProgressKind
{
    None,
    Download,
    Verify,
}

/// <summary>
/// Allocation-free parser for Legendary's progress output. Download progress is a five-line block logged by the
/// DLManager once per second on stderr (downloader/mp/manager.py:766-775):
/// <code>
/// [DLManager] INFO: = Progress: 12.34% (4321/35000), Running for 00:01:23, ETA: 00:09:51
/// [DLManager] INFO:  - Downloaded: 812.50 MiB, Written: 1024.00 MiB
/// [DLManager] INFO:  - Cache usage: 96.00 MiB, active tasks: 16
/// [DLManager] INFO:  + Download\t- 55.20 MiB/s (raw) / 120.40 MiB/s (decompressed)
/// [DLManager] INFO:  + Disk\t- 118.00 MiB/s (write) / 0.00 MiB/s (read)
/// </code>
/// A snapshot is published at the block's last line, or when a new header arrives while a block is still open (lost
/// or truncated lines never stall the display). Verify progress is stdout, one CR-terminated line per file
/// (cli.py:1315) plus per-1% lines for files over 512 MiB (lfs/utils.py:140), which we interpolate into the overall
/// percentage once the total size can be estimated.
/// </summary>
internal sealed class ProgressParser
{
    private const double MiB = 1024 * 1024;

    private LegendaryProgress _current;
    private bool _blockOpen;

    // verify interpolation state
    private double _verifyFilePercent;
    private double _largeFileSizeMiB;
    private double _largeFileStartPercent = -1;
    private double? _verifyTotalMiB;

    public LegendaryProgress Snapshot { get; private set; }

    public ProgressKind SnapshotKind { get; private set; }

    /// <summary>
    /// Returns true if <paramref name="line"/> is a progress line (the caller should not treat it as a log message).
    /// <paramref name="published"/> is true when <see cref="Snapshot"/> holds a new value.
    /// </summary>
    public bool TryParse(ReadOnlySpan<char> line, out bool published)
    {
        published = false;
        if (line.Length == 0)
        {
            return false;
        }

        if (line[0] == '[')
        {
            return TryParseDownload(line, ref published);
        }

        if (line[0] is 'V' or '=')
        {
            return TryParseVerify(line, ref published);
        }

        return false;
    }

    /// <summary>Publishes a block that never received its last line (call at end of stream).</summary>
    public bool TryFlush()
    {
        if (!_blockOpen)
        {
            return false;
        }

        _blockOpen = false;
        Publish(ProgressKind.Download);
        return true;
    }

    private bool TryParseDownload(ReadOnlySpan<char> line, ref bool published)
    {
        // "[<logger>] INFO: <message>"
        var close = line.IndexOf("] INFO: ", StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        var msg = line[(close + 8)..];
        if (msg.StartsWith("= Progress: ", StringComparison.Ordinal))
        {
            var next = _current;
            if (!ParseHeader(msg[12..], ref next))
            {
                return false;
            }

            if (_blockOpen)
            {
                // Previous block lost its tail: publish what it had before taking the new header's values.
                Publish(ProgressKind.Download);
                published = true;
            }

            _current = next;
            _blockOpen = true;
            return true;
        }

        if (msg.StartsWith(" - Downloaded: ", StringComparison.Ordinal))
        {
            // " - Downloaded: 812.50 MiB, Written: 1024.00 MiB"
            var rest = msg[15..];
            if (!TakeNumber(ref rest, " MiB, Written: ", out var dl) || !TakeNumber(ref rest, " MiB", out var wr))
            {
                return false;
            }

            _current = _current with { DownloadedBytes = (long)(dl * MiB), WrittenBytes = (long)(wr * MiB) };
            _blockOpen = true;
            return true;
        }

        if (msg.StartsWith(" - Cache usage: ", StringComparison.Ordinal))
        {
            return true; // recognised (kept out of the log), not needed by the UI
        }

        if (msg.StartsWith(" + Download\t- ", StringComparison.Ordinal))
        {
            // " + Download\t- 55.20 MiB/s (raw) / 120.40 MiB/s (decompressed)"
            var rest = msg[14..];
            if (!TakeNumber(ref rest, " MiB/s (raw) / ", out var raw) || !TakeNumber(ref rest, " MiB/s (decompressed)", out var dec))
            {
                return false;
            }

            _current = _current with { DownloadBytesPerSecond = raw * MiB, DecompressedBytesPerSecond = dec * MiB };
            _blockOpen = true;
            return true;
        }

        if (msg.StartsWith(" + Disk\t- ", StringComparison.Ordinal))
        {
            // " + Disk\t- 118.00 MiB/s (write) / 0.00 MiB/s (read)"
            var rest = msg[10..];
            if (!TakeNumber(ref rest, " MiB/s (write) / ", out var w) || !TakeNumber(ref rest, " MiB/s (read)", out var r))
            {
                return false;
            }

            _current = _current with { DiskWriteBytesPerSecond = w * MiB, DiskReadBytesPerSecond = r * MiB };
            _blockOpen = false;
            Publish(ProgressKind.Download);
            published = true;
            return true;
        }

        return false;
    }

    private static bool ParseHeader(ReadOnlySpan<char> s, ref LegendaryProgress p)
    {
        // "12.34% (4321/35000), Running for 00:01:23, ETA: 00:09:51"
        if (!TakeNumber(ref s, "% (", out var pct) ||
            !TakeLong(ref s, "/", out var done) ||
            !TakeLong(ref s, "), Running for ", out var total))
        {
            return false;
        }

        var etaAt = s.IndexOf(", ETA: ", StringComparison.Ordinal);
        if (etaAt < 0 || !TryParseClock(s[..etaAt], out var elapsed) || !TryParseClock(s[(etaAt + 7)..].TrimEnd(), out var eta))
        {
            return false;
        }

        // Legendary prints ETA 00:00:00 until the first chunk completes.
        p = p with
        {
            Percent = pct,
            ProcessedChunks = done,
            TotalChunks = total,
            ElapsedSeconds = elapsed,
            EtaSeconds = done > 0 ? eta : null,
        };
        return true;
    }

    private bool TryParseVerify(ReadOnlySpan<char> line, ref bool published)
    {
        line = line.TrimEnd(); // trailing "\t"
        if (line.StartsWith("Verification progress: ", StringComparison.Ordinal))
        {
            // "Verification progress: 123/456 (27.0%) [210.5 MiB/s]"
            var s = line[23..];
            if (!TakeLong(ref s, "/", out var num) ||
                !TakeLong(ref s, " (", out var total) ||
                !TakeNumber(ref s, "%) [", out var pct) ||
                !TakeNumber(ref s, " MiB/s]", out var speed))
            {
                return false;
            }

            if (_largeFileStartPercent >= 0 && _largeFileSizeMiB > 0)
            {
                var delta = pct - _largeFileStartPercent;
                if (delta >= 0.5)
                {
                    _verifyTotalMiB = _largeFileSizeMiB / (delta / 100.0);
                }

                _largeFileStartPercent = -1;
            }

            _verifyFilePercent = pct;
            _current = new LegendaryProgress(pct, num, total, 0, null, 0, 0, 0, 0, 0, speed * MiB);
            Publish(ProgressKind.Verify);
            published = true;
            return true;
        }

        if (line.StartsWith("=> Verifying large file \"", StringComparison.Ordinal))
        {
            // "=> Verifying large file "<path>": 45% (1234.0/2740.0 MiB) [200.1 MiB/s]"
            var end = line.LastIndexOf("\": ", StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }

            var s = line[(end + 3)..];
            if (!TakeNumber(ref s, "% (", out _) ||
                !TakeNumber(ref s, "/", out var pos) ||
                !TakeNumber(ref s, " MiB) [", out var size) ||
                !TakeNumber(ref s, " MiB/s]", out var speed))
            {
                return false;
            }

            if (_largeFileStartPercent < 0)
            {
                _largeFileStartPercent = _verifyFilePercent;
            }

            _largeFileSizeMiB = size;
            var overall = _verifyFilePercent;
            if (_verifyTotalMiB is > 0 and var totalMiB)
            {
                overall = Math.Min(100, _largeFileStartPercent + Math.Min(pos, size) / totalMiB * 100);
            }

            _current = _current with { Percent = Math.Max(overall, _verifyFilePercent), DiskReadBytesPerSecond = speed * MiB };
            Publish(ProgressKind.Verify);
            published = true;
            return true;
        }

        return false;
    }

    private void Publish(ProgressKind kind)
    {
        Snapshot = _current;
        SnapshotKind = kind;
    }

    /// <summary>Parses a number up to <paramref name="terminator"/> and advances past it.</summary>
    private static bool TakeNumber(ref ReadOnlySpan<char> s, ReadOnlySpan<char> terminator, out double value)
    {
        var at = s.IndexOf(terminator, StringComparison.Ordinal);
        if (at <= 0 || !double.TryParse(s[..at], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }

        s = s[(at + terminator.Length)..];
        return true;
    }

    private static bool TakeLong(ref ReadOnlySpan<char> s, ReadOnlySpan<char> terminator, out long value)
    {
        var at = s.IndexOf(terminator, StringComparison.Ordinal);
        if (at <= 0 || !long.TryParse(s[..at], NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            value = 0;
            return false;
        }

        s = s[(at + terminator.Length)..];
        return true;
    }

    /// <summary>"HH:MM:SS" where HH may have more than two digits.</summary>
    private static bool TryParseClock(ReadOnlySpan<char> s, out long seconds)
    {
        seconds = 0;
        var c1 = s.IndexOf(':');
        var c2 = c1 < 0 ? -1 : s[(c1 + 1)..].IndexOf(':');
        if (c1 <= 0 || c2 != 2)
        {
            return false;
        }

        if (!long.TryParse(s[..c1], NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !long.TryParse(s.Slice(c1 + 1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
            !long.TryParse(s[(c1 + 4)..], NumberStyles.None, CultureInfo.InvariantCulture, out var sec))
        {
            return false;
        }

        seconds = h * 3600 + m * 60 + sec;
        return true;
    }
}
