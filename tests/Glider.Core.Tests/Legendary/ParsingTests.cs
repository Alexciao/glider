using System.Buffers;
using System.Text;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

public class ProgressParserTests
{
    private const double MiB = 1024 * 1024;

    [Fact]
    public void ParsesEveryBlockOfAFreshInstall()
    {
        var (_, progress) = Fixture.Replay("synthetic-install-fresh.stderr.txt");
        Assert.Equal(16, progress.Count);

        var first = progress[0];
        Assert.Equal(0, first.Percent);
        Assert.Null(first.EtaSeconds); // Legendary prints 00:00:00 before the first chunk lands
        Assert.Equal(62018, first.TotalChunks);

        var last = progress[^1];
        Assert.Equal(100, last.Percent);
        Assert.Equal(last.TotalChunks, last.ProcessedChunks);
        Assert.Equal(361, last.ElapsedSeconds);
        Assert.Equal((long)(30522.53 * MiB), last.DownloadedBytes, tolerance: 1);

        for (var i = 1; i < progress.Count; i++)
        {
            Assert.True(progress[i].Percent >= progress[i - 1].Percent);
            Assert.True(progress[i].DownloadedBytes >= progress[i - 1].DownloadedBytes);
        }

        var mid = progress[5];
        Assert.InRange(mid.DownloadBytesPerSecond / MiB, 70, 115);
        Assert.Equal(mid.DownloadBytesPerSecond * 1.62, mid.DecompressedBytesPerSecond, tolerance: 0.02 * MiB);
        Assert.NotNull(mid.EtaSeconds);
    }

    [Fact]
    public void ParsesExactValuesOfOneBlock()
    {
        var parser = new ProgressParser();
        string[] block =
        [
            "[DLManager] INFO: = Progress: 12.34% (4321/35000), Running for 100:01:23, ETA: 00:09:51",
            "[DLManager] INFO:  - Downloaded: 812.50 MiB, Written: 1024.00 MiB",
            "[DLManager] INFO:  - Cache usage: 96.00 MiB, active tasks: 16",
            "[DLManager] INFO:  + Download\t- 55.20 MiB/s (raw) / 120.40 MiB/s (decompressed)",
            "[DLManager] INFO:  + Disk\t- 118.00 MiB/s (write) / 0.50 MiB/s (read)",
        ];
        var published = 0;
        foreach (var line in block)
        {
            Assert.True(parser.TryParse(line, out var p));
            published += p ? 1 : 0;
        }

        Assert.Equal(1, published);
        var s = parser.Snapshot;
        Assert.Equal(12.34, s.Percent);
        Assert.Equal(4321, s.ProcessedChunks);
        Assert.Equal(35000, s.TotalChunks);
        Assert.Equal(100 * 3600 + 83, s.ElapsedSeconds);
        Assert.Equal(591, s.EtaSeconds);
        Assert.Equal((long)(812.5 * MiB), s.DownloadedBytes);
        Assert.Equal((long)(1024 * MiB), s.WrittenBytes);
        Assert.Equal(55.2 * MiB, s.DownloadBytesPerSecond, tolerance: 1);
        Assert.Equal(120.4 * MiB, s.DecompressedBytesPerSecond, tolerance: 1);
        Assert.Equal(118 * MiB, s.DiskWriteBytesPerSecond, tolerance: 1);
        Assert.Equal(0.5 * MiB, s.DiskReadBytesPerSecond, tolerance: 1);
        Assert.Equal(ProgressKind.Download, parser.SnapshotKind);
    }

    [Fact]
    public void PartialBlocksStillPublish()
    {
        var (_, progress) = Fixture.Replay("synthetic-install-partialblock.stderr.txt");
        Assert.Equal([10.0, 20.0, 30.0], progress.Select(p => p.Percent));
        Assert.Equal((long)(50 * MiB), progress[0].DownloadedBytes); // first block lost its speed lines
        Assert.Equal(5 * MiB, progress[1].DownloadBytesPerSecond, tolerance: 1);
    }

    [Theory]
    [InlineData("[DLManager] INFO: = Progress: abc% (1/2), Running for 00:00:01, ETA: 00:00:01")]
    [InlineData("[DLManager] INFO: = Progress: 1.00% (1/2), Running for 0:0:1, ETA: 00:00:01")]
    [InlineData("[DLManager] WARNING: = Progress: 1.00% (1/2), Running for 00:00:01, ETA: 00:00:01")]
    [InlineData("[cli] INFO: Download size: 100.00 MiB (Compression savings: 19.0%)")]
    [InlineData("Verification progress: 1/x (1.0%) [1.0 MiB/s]")]
    [InlineData("")]
    public void RejectsNonProgressLines(string line) => Assert.False(new ProgressParser().TryParse(line, out _));

    [Fact]
    public void ParsesVerifyProgress()
    {
        var (_, progress) = Fixture.Replay(null, "synthetic-verify-ok.stdout.txt");
        Assert.Equal(13, progress.Count); // 12 CR-terminated updates + the final LF one
        Assert.Equal(100, progress[^1].Percent);
        Assert.Equal(12, progress[^1].TotalChunks);
        Assert.Equal(980.4 * MiB, progress[^1].DiskReadBytesPerSecond, tolerance: 1);
    }

    [Fact]
    public void InterpolatesLargeFilesOnceTheTotalIsKnown()
    {
        var (_, progress) = Fixture.Replay(null, "synthetic-verify-largefile.stdout.txt");
        // file 4 (2 GiB) runs from 34.6% to 96.2%; the estimate from file 2 lets us move smoothly in between
        var during = progress.SkipWhile(p => p.ProcessedChunks != 3).Skip(1).TakeWhile(p => p.ProcessedChunks == 3).ToList();
        Assert.True(during.Count > 50);
        Assert.InRange(during[during.Count / 2].Percent, 55, 75);
        for (var i = 1; i < during.Count; i++)
        {
            Assert.True(during[i].Percent >= during[i - 1].Percent);
        }

        Assert.All(progress, p => Assert.InRange(p.Percent, 0, 100));
    }

    [Fact]
    public void ParsingAProgressBlockDoesNotAllocate()
    {
        var parser = new ProgressParser();
        string[] block =
        [
            "[DLManager] INFO: = Progress: 12.34% (4321/35000), Running for 00:01:23, ETA: 00:09:51",
            "[DLManager] INFO:  - Downloaded: 812.50 MiB, Written: 1024.00 MiB",
            "[DLManager] INFO:  - Cache usage: 96.00 MiB, active tasks: 16",
            "[DLManager] INFO:  + Download\t- 55.20 MiB/s (raw) / 120.40 MiB/s (decompressed)",
            "[DLManager] INFO:  + Disk\t- 118.00 MiB/s (write) / 0.00 MiB/s (read)",
        ];
        foreach (var l in block)
        {
            _ = parser.TryParse(l, out _); // warm up
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            foreach (var l in block)
            {
                _ = parser.TryParse(l, out _);
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

public class EncodingAndLineSplittingTests
{
    [Fact]
    public void DecodesRealAnsiStderrWithPythonEscapes()
    {
        // Real bytes from legendary 0.21.1: cp1252 for ü/ï/ö/é, \u65e5\u672c for 日本 (legendary.md §0.3)
        var lines = Fixture.Lines("encoding-test-default.stderr.bin");
        Assert.Equal(["[cli] ERROR: Specified path \"D:\\ünïcödé日本\" does not exist!"], lines);
        Assert.Equal(Fixture.Bytes("encoding-test-default.stderr.bin"), Fixture.Bytes("encoding-test-PYTHONUTF8.stderr.bin"));
    }

    [Theory]
    [InlineData(@"D:\x86\tools", @"D:\x86\tools")]               // \x is never unescaped
    [InlineData(@"C:\Users\uabc\Games", @"C:\Users\uabc\Games")]  // not hex
    [InlineData(@"C:\u0041", @"C:\u0041")]                        // ASCII is encodable: Python never escapes it
    [InlineData(@"C:\u00e9", @"C:\u00e9")]                        // é is in cp1252: a literal backslash-u, not an escape
    [InlineData(@"name \u65e5\u672c!", "name 日本!")]
    [InlineData(@"emoji \U0001f600", "emoji 😀")]
    [InlineData(@"trailing \u65e", @"trailing \u65e")]
    public void UnescapesOnlyWhatPythonCouldHaveProduced(string input, string expected)
    {
        var chars = input.ToCharArray();
        var n = LegendaryEncoding.UnescapeBackslashReplace(chars, LegendaryEncoding.Stderr);
        Assert.Equal(expected, new string(chars, 0, n));
    }

    [Fact]
    public void SplitsOnCrLfAndCrlfAcrossSegments()
    {
        var lines = new List<string>();
        var pump = new LinePump(Encoding.UTF8, unescapePython: false, l => lines.Add(new string(l)));
        var bytes = Encoding.UTF8.GetBytes("one\r\ntwo\rthree\nfour\r\n\r\nfi");
        // split into 1-byte segments to force multi-segment lines and CR/LF on segment boundaries
        var seq = Segments(bytes);
        pump.Consume(ref seq, final: false);
        Assert.Equal(["one", "two", "three", "four"], lines);
        Assert.Equal(2, seq.Length); // "fi" waits for more data

        var rest = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("fi\u00e9 ve"));
        pump.Consume(ref rest, final: true);
        Assert.Equal("fié ve", lines[^1]);
    }

    [Fact]
    public void BreaksPathologicallyLongLines()
    {
        var count = 0;
        var pump = new LinePump(Encoding.ASCII, false, _ => count++);
        var seq = new ReadOnlySequence<byte>(new byte[LinePump.MaxLineBytes * 2 + 10]);
        pump.Consume(ref seq, final: false);
        Assert.Equal(2, count);
    }

    private static ReadOnlySequence<byte> Segments(byte[] bytes)
    {
        var first = new Segment(bytes.AsMemory(0, 1), 0);
        var last = first;
        for (var i = 1; i < bytes.Length; i++)
        {
            last = last.Append(bytes.AsMemory(i, 1));
        }

        return new ReadOnlySequence<byte>(first, 0, last, 1);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long index)
        {
            Memory = memory;
            RunningIndex = index;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}

public class LegendaryConfigFileTests
{
    private static readonly KeyValuePair<string, string>[] Managed =
    [
        new("disable_update_check", "true"),
        new("disable_update_notice", "true"),
        new("egl_sync", "false"),
    ];

    [Fact]
    public void CreatesTheSectionWhenMissing()
    {
        var text = LegendaryConfigFile.Apply(null, Managed)!;
        Assert.Equal("[Legendary]\ndisable_update_check = true\ndisable_update_notice = true\negl_sync = false\n", text);
        Assert.Null(LegendaryConfigFile.Apply(text, Managed)); // idempotent: no rewrite, mtime untouched
    }

    [Fact]
    public void PreservesLegendaryOwnStateAndReplacesOnlyManagedKeys()
    {
        // what Legendary itself writes (valueless ';' keys, install_tags after the SDL choice, cli.py:971)
        const string existing = """
            [Legendary]
            ; disables the automatic update check
            disable_update_check = false
            ; disables the notice about an available update on exit
            disable_update_notice = false
            egl_sync = true
            egl_sync = true
            locale = de-DE

            [Fortnite]
            install_tags = ,Startup,FrontEnd,FortniteBR
            start_params = -d3d12
                -continued
            """;
        var text = LegendaryConfigFile.Apply(existing, Managed)!;

        Assert.Contains("; disables the automatic update check\ndisable_update_check = true\n", text);
        Assert.Contains("disable_update_notice = true\negl_sync = false\nlocale = de-DE\n", text);
        Assert.Single(text.Split('\n'), l => l.StartsWith("egl_sync", StringComparison.Ordinal));
        Assert.Contains("[Fortnite]\ninstall_tags = ,Startup,FrontEnd,FortniteBR\nstart_params = -d3d12\n    -continued\n", text);
    }

    [Fact]
    public void AppendsMissingKeysAtTheEndOfTheSection()
    {
        const string existing = "[Fortnite]\ninstall_tags = ,Startup\n\n[Legendary]\nlocale = en-US\n\n[default]\nno_wine = true\n";
        var text = LegendaryConfigFile.Apply(existing, Managed)!;
        Assert.Contains("[Legendary]\nlocale = en-US\ndisable_update_check = true\ndisable_update_notice = true\negl_sync = false\n\n[default]", text);
    }

    [Fact]
    public async Task LeavesTheUpdateCheckOnUntilApiDataExists()
    {
        using var dir = new TempDir();
        Assert.Contains(LegendaryConfigFile.ManagedValues(dir.Path), kv => kv is { Key: "disable_update_check", Value: "false" });
        await File.WriteAllTextAsync(dir.Combine("version.json"), """{"data": {}, "last_update": 1}""", TestContext.Current.CancellationToken);
        Assert.Contains(LegendaryConfigFile.ManagedValues(dir.Path), kv => kv is { Key: "disable_update_check", Value: "true" });

        Assert.True(await LegendaryConfigFile.EnsureAsync(dir.Path, TestContext.Current.CancellationToken));
        Assert.False(await LegendaryConfigFile.EnsureAsync(dir.Path, TestContext.Current.CancellationToken));
    }
}

public class CommandLineSplitterTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("-a -b", new[] { "-a", "-b" })]
    [InlineData("  -x   \"two words\"  ", new[] { "-x", "two words" })]
    [InlineData("-path=\"C:\\My Games\\\\\" -n", new[] { "-path=C:\\My Games\\", "-n" })]
    [InlineData("a\\\"b \"c\"\"d\"", new[] { "a\"b", "c\"d" })]
    public void SplitsLikeWindows(string input, string[] expected) => Assert.Equal(expected, CommandLineSplitter.Split(input));
}
