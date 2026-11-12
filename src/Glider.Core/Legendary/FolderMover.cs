namespace Glider.Core.Legendary;

/// <summary>
/// Copies an install tree to another volume with progress (Legendary's <c>move</c> is a same-volume rename only,
/// cli.py:2650). The caller registers the new path with <c>move --skip-move</c> and only then deletes the source, so
/// a failure or cancel at any point leaves the original install intact.
/// </summary>
internal static class FolderMover
{
    private const int BufferSize = 1 << 20;

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    public static bool PathsEqual(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>True if <paramref name="child"/> is <paramref name="parent"/> or inside it.</summary>
    public static bool IsInside(string child, string parent)
    {
        var c = Normalize(child) + Path.DirectorySeparatorChar;
        var p = Normalize(parent) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static long TreeSize(string root) =>
        new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
            .Sum(f => f.Length);

    public static async Task CopyTreeAsync(string source, string destination, IProgress<LegendaryProgress>? progress, TimeProvider time, CancellationToken ct)
    {
        var src = new DirectoryInfo(source);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        var files = src.EnumerateFiles("*", options).ToList();
        var total = files.Sum(f => f.Length);
        Directory.CreateDirectory(destination);
        foreach (var dir in src.EnumerateDirectories("*", options))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir.FullName)));
        }

        var buffer = new byte[BufferSize];
        long copied = 0;
        long lastReportBytes = 0;
        var lastReport = time.GetTimestamp();
        var started = lastReport;
        var done = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file.FullName));
            var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    output.SetLength(file.Length);
                    int n;
                    while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        copied += n;
                        var now = time.GetTimestamp();
                        var dt = time.GetElapsedTime(lastReport, now).TotalSeconds;
                        if (dt >= 0.25)
                        {
                            Report(progress, copied, total, done, files.Count, (copied - lastReportBytes) / dt, time.GetElapsedTime(started, now));
                            lastReport = now;
                            lastReportBytes = copied;
                        }
                    }
                }
            }

            File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
            done++;
        }

        Report(progress, copied, total, done, files.Count, 0, time.GetElapsedTime(started));
    }

    public static void TryDeleteTree(string path, ILog log)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn("legendary", $"Could not fully delete \"{path}\": {e.Message}");
        }
    }

    private static void Report(IProgress<LegendaryProgress>? progress, long copied, long total, int done, int count, double bytesPerSecond, TimeSpan elapsed)
    {
        var pct = total > 0 ? copied * 100.0 / total : 100;
        progress?.Report(new LegendaryProgress(pct, done, count, (long)elapsed.TotalSeconds, null, 0, copied, 0, 0, bytesPerSecond, 0));
    }
}
