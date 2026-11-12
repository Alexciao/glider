namespace Glider.Core.Infrastructure;

/// <summary>
/// Crash-safe file writes: write a sibling temp file, flush it to disk, then swap it over the target
/// (<see cref="File.Replace(string,string,string?,bool)"/> when the target exists, a rename otherwise).
/// A reader never observes a half-written file; after a power cut you get either the old or the new content.
/// </summary>
public static class AtomicFile
{
    // ~3.5 s in total: Windows can't rename over a file that another process has open at that instant (no POSIX
    // rename semantics), and antivirus scanners briefly open freshly written files.
    private static readonly int[] RetryDelaysMs = [10, 25, 50, 100, 200, 400, 700, 1000, 1000];

    public static string BackupPathFor(string path) => path + ".bak";

    public static Task WriteAllTextAsync(string path, string content, bool keepBackup = false, CancellationToken ct = default) =>
        WriteAllBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(content), keepBackup, ct);

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="content"/>.</summary>
    /// <param name="keepBackup">Keep the previous version as <c>path.bak</c> (used as a fallback when the main file is unreadable).</param>
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, bool keepBackup = false, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new ArgumentException("Path has no directory.", nameof(path));
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (fs.ConfigureAwait(false))
            {
                await fs.WriteAsync(content, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                // FlushFileBuffers: the data must be on disk before the rename makes it visible.
                fs.Flush(flushToDisk: true);
            }

            await SwapInAsync(tmp, full, keepBackup ? BackupPathFor(full) : null, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>Reads a file that other processes may be writing/renaming (shares read/write/delete, retries transient locks).</summary>
    public static async Task<byte[]?> TryReadAllBytesAsync(string path, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                await using (fs.ConfigureAwait(false))
                {
                    var buffer = new byte[fs.Length];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var n = await fs.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                        if (n == 0)
                        {
                            break;
                        }

                        read += n;
                    }

                    return read == buffer.Length ? buffer : buffer[..read];
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < RetryDelaysMs.Length)
            {
                await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false);
            }
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task SwapInAsync(string tmp, string target, string? backup, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(target))
                {
                    if (backup is not null)
                    {
                        File.Replace(tmp, target, backup, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(tmp, target, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tmp, target, overwrite: true);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < RetryDelaysMs.Length)
            {
                // Antivirus scanners and indexers briefly hold new files open; back off and retry.
                await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false);
            }
        }
    }
}
