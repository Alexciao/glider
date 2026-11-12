using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Glider.Core.Contracts;
using Glider.Core.Legendary;

namespace Glider.Core.Tests.TestSupport;

/// <summary>A unique temp directory, deleted (best effort) on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "glider-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>Captures log lines in memory.</summary>
public sealed class TestLog : ILog
{
    private readonly ConcurrentQueue<string> _lines = new();

    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    public string CurrentFile => "test.log";

    public IReadOnlyCollection<string> Lines => _lines;

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level >= MinimumLevel)
        {
            _lines.Enqueue($"{level} [{category}] {message}{(exception is null ? "" : " " + exception.Message)}");
        }
    }

    public string Tail(int maxLines) => string.Join('\n', _lines.TakeLast(maxLines));

    public bool Contains(string text) => _lines.Any(l => l.Contains(text, StringComparison.Ordinal));
}

/// <summary>Deterministic clock with manually fired timers.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        t.Change(dueTime, period);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_lock)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero ? _now + next.Period : null;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Loads fixtures and pushes them through the same decode/split path the runner uses.</summary>
public static class Fixture
{
    /// <summary>The code page the stderr fixtures were captured/rendered in (Western Windows), independent of this machine's ACP.</summary>
    public static Encoding Cp1252 { get; } = CreateCp1252();

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "legendary");

    public static string PathOf(string name) => Path.Combine(Dir, name);

    public static byte[] Bytes(string name) => File.ReadAllBytes(PathOf(name));

    /// <summary>Decoded lines (header comment lines of synthetic fixtures removed).</summary>
    public static List<string> Lines(string name, bool stdout = false)
    {
        var lines = new List<string>();
        var pump = new LinePump(stdout ? LegendaryEncoding.Stdout : Cp1252, unescapePython: !stdout, l => lines.Add(new string(l)));
        var seq = new ReadOnlySequence<byte>(Bytes(name));
        pump.Consume(ref seq, final: true);
        return lines.Where(l => !l.StartsWith("# Synthetic fixture", StringComparison.Ordinal)).ToList();
    }

    private static Encoding CreateCp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>Replays stderr (+ optional stdout) fixtures like a run: progress lines to the parser, the rest to the facts.</summary>
    internal static (LegendaryRunResult Run, List<LegendaryProgress> Progress) Replay(string? stderr, string? stdout = null, int exitCode = 0, bool stalled = false)
    {
        var facts = new LegendaryOutputFacts();
        var parser = new ProgressParser();
        var progress = new List<LegendaryProgress>();
        var tail = new List<string>();
        var sb = new StringBuilder();
        void Feed(string line, bool isStdout)
        {
            if (parser.TryParse(line, out var published))
            {
                if (published)
                {
                    progress.Add(parser.Snapshot);
                }

                return;
            }

            facts.Observe(line, isStdout);
            tail.Add(line);
            if (isStdout)
            {
                sb.Append(line).Append('\n');
            }
        }

        if (stdout is not null)
        {
            foreach (var l in Lines(stdout, stdout: true))
            {
                Feed(l, true);
            }
        }

        if (stderr is not null)
        {
            foreach (var l in Lines(stderr))
            {
                Feed(l, false);
            }
        }

        if (parser.TryFlush())
        {
            progress.Add(parser.Snapshot);
        }

        var run = new LegendaryRunResult(exitCode, sb.ToString(), tail, facts, stalled, false, false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(90));
        return (run, progress);
    }
}

/// <summary>An AppPaths over a temp root whose legendary.exe is the FakeLegendary test tool, plus script helpers.</summary>
public sealed class FakeLegendaryEnv : IDisposable
{
    private readonly TempDir _dir = new();

    public FakeLegendaryEnv()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "FakeLegendary.exe" : "FakeLegendary");
        Paths = new AppPaths(_dir.Path, exe);
        Paths.EnsureCreated();
        foreach (var d in new[] { "fake", "manifests", "metadata", "tmp" }) // what Legendary creates on first run
        {
            Directory.CreateDirectory(Path.Combine(Paths.LegendaryConfig, d));
        }
    }

    public AppPaths Paths { get; }

    public TestLog Log { get; } = new();

    public string Root => _dir.Path;

    public string Config => Paths.LegendaryConfig;

    public string FakeDir => Path.Combine(Paths.LegendaryConfig, "fake");

    /// <summary>Script for every invocation of <paramref name="command"/> (or only the n-th when <paramref name="invocation"/> is set).</summary>
    public void Script(string command, string script, int? invocation = null)
    {
        var name = invocation is { } n ? $"{command}.{n}.script" : $"{command}.script";
        File.WriteAllText(Path.Combine(FakeDir, name), script.ReplaceLineEndings("\n"));
    }

    /// <summary>Copies a fixture into the fake dir so scripts can reference it by name.</summary>
    public void UseFixture(string name) => File.Copy(Fixture.PathOf(name), Path.Combine(FakeDir, name), overwrite: true);

    public IReadOnlyList<(string Command, string[] Args)> Invocations()
    {
        var file = Path.Combine(FakeDir, "invocations.log");
        if (!File.Exists(file))
        {
            return [];
        }

        return File.ReadAllLines(file).Where(l => l.Length > 0).Select(l =>
        {
            var tab = l.IndexOf('\t');
            return (l[..tab], l[(tab + 1)..].Split('\u001f'));
        }).ToList();
    }

    public string ResumeFile => Path.Combine(Config, "tmp", "Fortnite.resume");

    public void Dispose() => _dir.Dispose();
}
