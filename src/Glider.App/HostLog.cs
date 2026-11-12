using System.Globalization;
using System.Text;

namespace Glider.App;

/// <summary>
/// Tiny append-only log for the host shell (window, tray, WebView2). Core keeps its own rolling log;
/// this writes <c>logs\host-yyyyMMdd.log</c> so the two never contend for one file. Never throws.
/// </summary>
internal static class HostLog
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static void Init(string logDirectory)
    {
        _dir = logDirectory;
        try
        {
            Directory.CreateDirectory(logDirectory);
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(logDirectory, "host-*.log"))
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    File.Delete(f);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Info(string message) => Write("INF", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WRN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var dir = _dir;
        if (dir is null)
        {
            return;
        }

        var sb = new StringBuilder(128);
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ').Append(level).Append(" [host] ").Append(message);
        if (ex is not null)
        {
            sb.Append(' ').Append(ex);
        }

        sb.AppendLine();
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(dir, $"host-{DateTime.Now:yyyyMMdd}.log"), sb.ToString());
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
