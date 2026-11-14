using System.Globalization;
using System.Text.Json;
using Glider.Core.Infrastructure;

namespace Glider.Core.Saves;

internal sealed class SaveBackupOptions
{
    /// <summary>Backups of a save that a download or "keep cloud" replaced: the ones that rescue progress.</summary>
    public int KeepOverwritten { get; init; } = 10;

    /// <summary>Copies of what was uploaded.</summary>
    public int KeepUploaded { get; init; } = 5;

    /// <summary>A save folder larger than this is not copied, and so is never overwritten automatically.</summary>
    public long MaxBackupBytes { get; init; } = 1L << 30;

    /// <summary>All backups of one game together; the oldest copies go first (the newest overwritten one always stays).</summary>
    public long MaxTotalBytes { get; init; } = 4L << 30;
}

internal sealed class SaveBackupException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed record SaveBackup(string Directory, SaveBackupInfo Info)
{
    public string FilesDirectory => Path.Combine(Directory, SaveBackups.FilesFolder);

    public string Name => Path.GetFileName(Directory);
}

/// <summary>
/// `backups/saves/&lt;app&gt;/&lt;timestamp&gt;/` — copies of the local save made before every overwrite (a download, "keep cloud")
/// and after every upload. A copy is built in a `.partial` folder and renamed when every file was copied and its size checked,
/// so a backup either exists whole or not at all; if one cannot be made the overwrite does not happen.
/// </summary>
internal sealed class SaveBackups
{
    public const string FilesFolder = "files";
    public const string InfoFile = "backup.json";

    private readonly string _root;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly SaveBackupOptions _options;

    public SaveBackups(string root, ILog log, TimeProvider time, SaveBackupOptions? options = null)
    {
        _root = root;
        _log = log;
        _time = time;
        _options = options ?? new SaveBackupOptions();
    }

    public string DirectoryFor(string app) => Path.Combine(_root, CleanName(app));

    public int Count(string app) => List(app).Count;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<SaveBackup> List(string app)
    {
        var dir = DirectoryFor(app);
        if (!System.IO.Directory.Exists(dir))
        {
            return [];
        }

        var list = new List<SaveBackup>();
        foreach (var d in System.IO.Directory.EnumerateDirectories(dir))
        {
            if (d.EndsWith(".partial", StringComparison.Ordinal))
            {
                continue;
            }

            list.Add(new SaveBackup(d, ReadInfo(d)));
        }

        return [.. list.OrderByDescending(b => b.Info.CreatedAt).ThenByDescending(b => b.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Copies <paramref name="files"/> (relative to <paramref name="saveFolder"/>) into a new backup. Returns null when there is
    /// nothing to copy. Throws <see cref="SaveBackupException"/> when it cannot be made completely.
    /// </summary>
    public SaveBackup? Create(string app, string saveFolder, LocalSaveSnapshot files, string reason)
    {
        if (!files.HasFiles)
        {
            return null;
        }

        if (files.TotalBytes > _options.MaxBackupBytes)
        {
            throw new SaveBackupException($"The save is {files.TotalBytes / (1024 * 1024)} MB, more than the {_options.MaxBackupBytes / (1024 * 1024)} MB Glider backs up.");
        }

        var appDir = DirectoryFor(app);
        var now = _time.GetUtcNow();
        var name = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var final = Path.Combine(appDir, name);
        for (var n = 2; System.IO.Directory.Exists(final) || System.IO.Directory.Exists(final + ".partial"); n++)
        {
            final = Path.Combine(appDir, $"{name}-{n}");
        }

        var partial = final + ".partial";
        try
        {
            System.IO.Directory.CreateDirectory(Path.Combine(partial, FilesFolder));
            foreach (var f in files.Files)
            {
                Copy(Path.Combine(saveFolder, f.Path), Path.Combine(partial, FilesFolder, f.Path), f.MtimeUtcTicks, f.Size);
            }

            var info = new SaveBackupInfo(reason, now, files.Files.Count, files.TotalBytes, saveFolder);
            File.WriteAllText(Path.Combine(partial, InfoFile), JsonSerializer.Serialize(info, SaveJsonContext.Default.SaveBackupInfo));
            System.IO.Directory.Move(partial, final);
            _log.Info("saves", $"{app}: backed up {info.FileCount} save file(s) ({info.Bytes} bytes, {reason}) to {final}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SaveBackupException)
        {
            TryDeleteTree(partial);
            throw new SaveBackupException("Couldn't back up the save first: " + e.Message, e);
        }

        Prune(app);
        return new SaveBackup(final, ReadInfo(final));
    }

    /// <summary>
    /// Puts a backup back: the filtered files that are not in it are removed (a half-finished download may have added some),
    /// the others are copied over with their original mtimes. Never touches files the filters exclude.
    /// </summary>
    public void Restore(SaveBackup backup, string saveFolder, SaveFilters filters)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = backup.FilesDirectory;
        foreach (var (full, rel) in SaveFolder.Walk(source, CancellationToken.None))
        {
            keep.Add(rel);
            var info = new FileInfo(full);
            Copy(full, Path.Combine(saveFolder, rel), info.LastWriteTimeUtc.ToFileTimeUtc(), info.Length);
        }

        foreach (var (full, rel) in SaveFolder.Walk(saveFolder, CancellationToken.None).ToList())
        {
            if (!keep.Contains(rel) && filters.Matches(rel))
            {
                try
                {
                    File.Delete(full);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.Warn("saves", $"couldn't remove {rel} while restoring a backup", e);
                }
            }
        }
    }

    /// <summary>Keeps the newest KeepOverwritten and KeepUploaded backups and stays under the size cap.</summary>
    public void Prune(string app)
    {
        var dir = DirectoryFor(app);
        if (!System.IO.Directory.Exists(dir))
        {
            return;
        }

        foreach (var stale in System.IO.Directory.EnumerateDirectories(dir, "*.partial"))
        {
            TryDeleteTree(stale);
        }

        var all = List(app).ToList(); // newest first
        var doomed = new List<SaveBackup>();
        void Trim(string reason, int keep) => doomed.AddRange(all.Where(b => b.Info.Reason == reason).Skip(keep));
        Trim(BackupReasons.Overwritten, _options.KeepOverwritten);
        Trim(BackupReasons.Uploaded, _options.KeepUploaded);

        var survivors = all.Except(doomed).ToList();
        var total = survivors.Sum(b => b.Info.Bytes);
        if (total > _options.MaxTotalBytes)
        {
            // Oldest uploaded copies first, then the oldest overwritten ones, but never the newest overwritten backup.
            var order = survivors.Where(b => b.Info.Reason != BackupReasons.Overwritten).OrderBy(b => b.Info.CreatedAt)
                .Concat(survivors.Where(b => b.Info.Reason == BackupReasons.Overwritten).Skip(1).OrderBy(b => b.Info.CreatedAt));
            foreach (var b in order)
            {
                if (total <= _options.MaxTotalBytes)
                {
                    break;
                }

                doomed.Add(b);
                total -= b.Info.Bytes;
            }
        }

        foreach (var b in doomed)
        {
            TryDeleteTree(b.Directory);
        }
    }

    private static SaveBackupInfo ReadInfo(string dir)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(dir, InfoFile));
            if (JsonSerializer.Deserialize(text, SaveJsonContext.Default.SaveBackupInfo) is { } info)
            {
                return info;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        // A folder somebody made by hand, or an unreadable info file: still a backup, counted as the valuable kind.
        var created = System.IO.Directory.GetCreationTimeUtc(dir);
        return new SaveBackupInfo(BackupReasons.Overwritten, new DateTimeOffset(created, TimeSpan.Zero), 0, 0, "");
    }

    private static void Copy(string from, string to, long mtimeTicks, long expectedSize)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan))
        using (var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
        {
            src.CopyTo(dst);
        }

        File.SetLastWriteTimeUtc(to, DateTime.FromFileTimeUtc(mtimeTicks));
        var copied = new FileInfo(to).Length;
        if (copied != expectedSize)
        {
            // The file changed while it was copied (a game writing), or the copy is short.
            throw new SaveBackupException($"{Path.GetFileName(from)} changed while it was copied ({expectedSize} bytes expected, {copied} copied).");
        }
    }

    private void TryDeleteTree(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn("saves", $"couldn't delete {path}", e);
        }
    }

    private static string CleanName(string app) => string.Concat(app.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0));
}
