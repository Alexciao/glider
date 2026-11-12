using System.Globalization;
using System.Text;
using Glider.Core.Contracts;

namespace Glider.Core.Egl;

/// <summary>
/// Writes a set of EGL files with Glider's safety rules: every existing target is first copied byte-for-byte to
/// <c>backups\egl\&lt;timestamp&gt;\…</c>; each file is then written to a temp file in the same directory and swapped
/// in with <see cref="File.Replace(string, string, string?)"/> (atomic on NTFS); sharing violations are retried briefly;
/// if any write fails, files already committed in this transaction are restored from the backup. Nothing is ever
/// deleted except files this transaction itself created.
/// </summary>
internal sealed class EglFileTransaction
{
    private readonly string _backupRoot;
    private readonly EglServiceOptions _options;
    private readonly ILog _log;
    private readonly List<Staged> _staged = [];

    private sealed record Staged(string Target, byte[] Content, string BackupRelative);

    public EglFileTransaction(string backupRoot, EglServiceOptions options, ILog log)
    {
        _backupRoot = backupRoot;
        _options = options;
        _log = log;
    }

    /// <summary>Directory of this transaction's backups (set once <see cref="CommitAsync"/> ran).</summary>
    public string? BackupDirectory { get; private set; }

    public int Count => _staged.Count;

    /// <summary>Queue a write. <paramref name="backupRelative"/> is the path under the backup folder.</summary>
    public void Stage(string target, byte[] content, string backupRelative) =>
        _staged.Add(new Staged(target, content, backupRelative));

    public async Task CommitAsync(CancellationToken ct)
    {
        if (_staged.Count == 0)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        var changed = new List<Staged>();
        foreach (var s in _staged)
        {
            if (File.Exists(s.Target) && FileEquals(s.Target, s.Content))
            {
                _log.Debug(EglService.LogCategory, $"Unchanged, not rewriting: {s.Target}");
                continue;
            }

            changed.Add(s);
        }

        if (changed.Count == 0)
        {
            return;
        }

        var backups = CreateBackups(changed);

        // From here on we don't honour cancellation mid-way: a half-applied set is worse than finishing.
        var committed = new List<(Staged File, string? Backup)>();
        try
        {
            foreach (var s in changed)
            {
                await WriteWithRetriesAsync(s.Target, s.Content).ConfigureAwait(false);
                committed.Add((s, backups.GetValueOrDefault(s.Target)));
                _log.Info(EglService.LogCategory, $"Wrote {s.Target}");
            }
        }
        catch (Exception ex)
        {
            Rollback(committed);
            throw ex is GliderException ? ex : new GliderException(Errors.EglWriteFailed(ex.Message), ex);
        }
    }

    private Dictionary<string, string> CreateBackups(List<Staged> changed)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existing = changed.Where(s => File.Exists(s.Target)).ToList();
        var stamp = _options.Time.GetUtcNow().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var dir = Path.Combine(_backupRoot, stamp);
        for (var i = 2; Directory.Exists(dir); i++)
        {
            dir = Path.Combine(_backupRoot, $"{stamp}-{i}");
        }

        Directory.CreateDirectory(dir);
        BackupDirectory = dir;
        var index = new StringBuilder();
        index.Append("Glider backup of Epic Games Launcher files, taken before Glider modified them.").Append('\n');
        index.Append("To restore, copy each file back to the original path listed below (with EGL closed).").Append('\n').Append('\n');
        try
        {
            foreach (var s in existing)
            {
                var dest = Path.Combine(dir, s.BackupRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(s.Target, dest, overwrite: false);
                map[s.Target] = dest;
                index.Append(s.BackupRelative).Append('\t').Append(s.Target).Append('\n');
            }

            foreach (var s in changed.Where(s => !map.ContainsKey(s.Target)))
            {
                index.Append("(new)").Append('\t').Append(s.Target).Append('\n');
            }

            File.WriteAllText(Path.Combine(dir, "backup-index.txt"), index.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No backup => no write. The originals are untouched at this point.
            throw new GliderException(Errors.EglWriteFailed($"Couldn't back up EGL files to {dir}: {ex.Message}"), ex);
        }

        _log.Info(EglService.LogCategory, $"Backed up {map.Count} EGL file(s) to {dir}");
        PruneOldBackups();
        return map;
    }

    private void PruneOldBackups()
    {
        try
        {
            var dirs = new DirectoryInfo(_backupRoot).GetDirectories()
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .Skip(_options.BackupsToKeep)
                .ToList();
            foreach (var d in dirs)
            {
                d.Delete(recursive: true); // Glider's own backup copies, never EGL files
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(EglService.LogCategory, "Couldn't prune old EGL backups", ex);
        }
    }

    private void Rollback(List<(Staged File, string? Backup)> committed)
    {
        for (var i = committed.Count - 1; i >= 0; i--)
        {
            var (file, backup) = committed[i];
            try
            {
                if (backup is not null)
                {
                    WriteAtomic(file.Target, File.ReadAllBytes(backup));
                    _log.Warn(EglService.LogCategory, $"Rolled back {file.Target} from {backup}");
                }
                else if (File.Exists(file.Target))
                {
                    File.Delete(file.Target); // created by this transaction, not an EGL file
                    _log.Warn(EglService.LogCategory, $"Rolled back (removed new file) {file.Target}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error(EglService.LogCategory, $"Rollback of {file.Target} failed; restore it from {backup ?? "(no backup)"}", ex);
            }
        }
    }

    private async Task WriteWithRetriesAsync(string target, byte[] content)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                WriteAtomic(target, content);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _options.WriteAttempts)
            {
                _log.Debug(EglService.LogCategory, $"{target} is locked ({ex.Message}); retry {attempt}/{_options.WriteAttempts - 1}");
                await Task.Delay(_options.WriteRetryDelay).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                throw ex is UnauthorizedAccessException
                    ? new GliderException(Errors.EglAccessDenied(target), ex)
                    : new GliderException(Errors.EglFileLocked(target), ex);
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is UnauthorizedAccessException ||
        (ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException && ex is not PathTooLongException);

    /// <summary>Temp file in the same directory, flushed to disk, then swapped in. The target is never partially written.</summary>
    internal void WriteAtomic(string target, byte[] content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.glider.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);
            }

            _options.BeforeCommitHook?.Invoke(target);
            if (File.Exists(target))
            {
                File.Replace(tmp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, target);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn(EglService.LogCategory, $"Couldn't remove temp file {tmp}", ex);
            }
        }
    }

    private static bool FileEquals(string path, byte[] content)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Length == content.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
