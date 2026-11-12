using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Glider.Core.Contracts;

namespace Glider.Core.Infrastructure;

/// <summary>
/// Rolling daily log (<c>glider-YYYYMMDD.log</c>) plus an in-memory ring of recent lines for <see cref="Tail"/>.
/// <see cref="Write"/> never blocks on I/O: entries go through a channel to a single background writer.
/// </summary>
public sealed class FileLog : ILog, IAsyncDisposable
{
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly int _retentionDays;
    private readonly Channel<Entry> _channel = Channel.CreateUnbounded<Entry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _ringLock = new();
    private readonly string[] _ring;
    private readonly Task _writer;
    private int _ringStart;
    private int _ringCount;
    private volatile int _minimumLevel;

    public FileLog(string directory, TimeProvider? time = null, LogLevel minimumLevel = LogLevel.Info, int tailCapacity = 2000, int retentionDays = 14)
    {
        _directory = Path.GetFullPath(directory);
        _time = time ?? TimeProvider.System;
        _retentionDays = retentionDays;
        _ring = new string[Math.Max(16, tailCapacity)];
        _minimumLevel = (int)minimumLevel;
        _writer = Task.Run(WriterLoopAsync);
    }

    public LogLevel MinimumLevel
    {
        get => (LogLevel)_minimumLevel;
        set => _minimumLevel = (int)value;
    }

    public string CurrentFile => FileFor(_time.GetLocalNow());

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        if ((int)level < _minimumLevel)
        {
            return;
        }

        var now = _time.GetLocalNow();
        var sb = new StringBuilder(64 + message.Length);
        sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append(' ').Append(Tag(level)).Append(" [").Append(category).Append("] ").Append(message);
        if (exception is not null)
        {
            sb.AppendLine().Append(exception);
        }

        var text = SecretRedactor.Redact(sb.ToString()); // log files get attached to bug reports
        AddToRing(text);
        _channel.Writer.TryWrite(new Entry(now, text, null));
    }

    public string Tail(int maxLines)
    {
        if (maxLines <= 0)
        {
            return "";
        }

        lock (_ringLock)
        {
            var take = Math.Min(maxLines, _ringCount);
            var sb = new StringBuilder();
            for (var i = _ringCount - take; i < _ringCount; i++)
            {
                sb.Append(_ring[(_ringStart + i) % _ring.Length]).Append('\n');
            }

            return sb.ToString();
        }
    }

    /// <summary>Completes once everything written so far is on disk.</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new Entry(default, null, done)))
        {
            done.TrySetResult(); // already disposed
        }

        return done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };

    private string FileFor(DateTimeOffset localTime) =>
        Path.Combine(_directory, $"glider-{localTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    private void AddToRing(string text)
    {
        lock (_ringLock)
        {
            foreach (var line in text.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (_ringCount < _ring.Length)
                {
                    _ring[(_ringStart + _ringCount) % _ring.Length] = l;
                    _ringCount++;
                }
                else
                {
                    _ring[_ringStart] = l;
                    _ringStart = (_ringStart + 1) % _ring.Length;
                }
            }
        }
    }

    private async Task WriterLoopAsync()
    {
        StreamWriter? writer = null;
        string? openPath = null;
        var cleaned = false;
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                List<TaskCompletionSource>? flushed = null;
                while (reader.TryRead(out var entry))
                {
                    if (entry.Flush is not null)
                    {
                        (flushed ??= []).Add(entry.Flush);
                        continue;
                    }

                    var path = FileFor(entry.Time);
                    if (path != openPath)
                    {
                        if (writer is not null)
                        {
                            await writer.DisposeAsync().ConfigureAwait(false);
                            writer = null;
                        }

                        openPath = path;
                        writer = TryOpen(path);
                        if (!cleaned)
                        {
                            cleaned = true;
                            DeleteOldLogs(entry.Time);
                        }
                    }

                    if (writer is not null)
                    {
                        try
                        {
                            await writer.WriteLineAsync(entry.Text).ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                            // Disk full or file yanked away: keep the in-memory tail, try again with the next file.
                            await writer.DisposeAsync().ConfigureAwait(false);
                            writer = null;
                            openPath = null;
                        }
                    }
                }

                if (writer is not null)
                {
                    try
                    {
                        await writer.FlushAsync().ConfigureAwait(false);
                        // NTFS reports a stale 0-byte size for a file held open for append until its metadata is committed.
                        // One FlushFileBuffers per drained batch keeps the on-disk length truthful for anyone looking.
                        if (writer.BaseStream is FileStream fs)
                        {
                            fs.Flush(flushToDisk: true);
                        }
                    }
                    catch (IOException)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                        writer = null;
                        openPath = null;
                    }
                }

                if (flushed is not null)
                {
                    foreach (var f in flushed)
                    {
                        f.TrySetResult();
                    }
                }
            }
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            while (reader.TryRead(out var rest))
            {
                rest.Flush?.TrySetResult();
            }
        }
    }

    private static StreamWriter? TryOpen(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            return new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n", AutoFlush = false };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void DeleteOldLogs(DateTimeOffset now)
    {
        try
        {
            var cutoff = DateOnly.FromDateTime(now.DateTime).AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "glider-*.log"))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (stem.Length == 15 &&
                    DateOnly.TryParseExact(stem.AsSpan(7), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) &&
                    day < cutoff)
                {
                    AtomicFile.TryDelete(file);
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

    private readonly record struct Entry(DateTimeOffset Time, string? Text, TaskCompletionSource? Flush);
}
