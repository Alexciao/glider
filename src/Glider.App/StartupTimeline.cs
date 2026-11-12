using System.Diagnostics;

namespace Glider.App;

/// <summary>Milestone timestamps (ms since the OS created the process). Always on: it is a handful of dictionary writes.</summary>
internal static class StartupTimeline
{
    private static readonly object Gate = new();
    private static readonly List<KeyValuePair<string, double>> Marks = [];
    private static long _mainTimestamp;
    private static double _mainOffsetMs;

    /// <summary>Call first thing in Main.</summary>
    public static void Begin()
    {
        _mainTimestamp = Stopwatch.GetTimestamp();
        using var self = Process.GetCurrentProcess();
        _mainOffsetMs = Math.Max(0, (DateTime.Now - self.StartTime).TotalMilliseconds);
        Mark("main");
    }

    public static void Mark(string name)
    {
        var ms = _mainOffsetMs + Stopwatch.GetElapsedTime(_mainTimestamp).TotalMilliseconds;
        lock (Gate)
        {
            if (!Marks.Exists(m => m.Key == name))
            {
                Marks.Add(new(name, Math.Round(ms, 1)));
            }
        }
    }

    public static IReadOnlyList<KeyValuePair<string, double>> Snapshot()
    {
        lock (Gate)
        {
            return [.. Marks];
        }
    }

    public static bool Has(string name)
    {
        lock (Gate)
        {
            return Marks.Exists(m => m.Key == name);
        }
    }
}
