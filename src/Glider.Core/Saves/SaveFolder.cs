using System.Security.Cryptography;

namespace Glider.Core.Saves;

/// <summary>
/// Reads a local save folder: the files that take part in the sync (include/exclude filters applied, so a game's log or
/// config file never makes the save look changed), with size, mtime and a SHA-256. Hashes are reused from the previous
/// snapshot while size and mtime match, so a routine check only reads files that changed.
/// </summary>
internal static class SaveFolder
{
    public static LocalSaveSnapshot Scan(string root, SaveFilters filters, IReadOnlyList<SaveFileEntry>? previous = null, bool hash = true, CancellationToken ct = default)
    {
        if (!Directory.Exists(root))
        {
            return LocalSaveSnapshot.Empty;
        }

        Dictionary<string, SaveFileEntry>? known = null;
        if (previous is { Count: > 0 })
        {
            known = new Dictionary<string, SaveFileEntry>(previous.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var f in previous)
            {
                known[f.Path] = f;
            }
        }

        var files = new List<SaveFileEntry>();
        foreach (var (full, rel) in Walk(root, ct))
        {
            if (!filters.Matches(rel))
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(full);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var ticks = info.LastWriteTimeUtc.ToFileTimeUtc();
            string? sha = null;
            if (known is not null && known.TryGetValue(rel, out var old) && old.Size == info.Length && old.MtimeUtcTicks == ticks)
            {
                sha = old.Hash;
            }

            if (sha is null && hash)
            {
                sha = HashOf(full);
            }

            files.Add(new SaveFileEntry(rel, info.Length, ticks, sha));
        }

        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new LocalSaveSnapshot(files);
    }

    /// <summary>Newest mtime of every file in the folder, filters ignored: what Legendary's own `check_savegame_state` looks at.</summary>
    public static DateTimeOffset? LatestMtimeUnfiltered(string root, CancellationToken ct = default)
    {
        long latest = 0;
        foreach (var (full, _) in Walk(root, ct))
        {
            try
            {
                latest = Math.Max(latest, new FileInfo(full).LastWriteTimeUtc.ToFileTimeUtc());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return latest == 0 ? null : DateTimeOffset.FromFileTime(latest).ToUniversalTime();
    }

    /// <summary>Every file below <paramref name="root"/> (hidden and system files included; directory links are not entered, like os.walk).</summary>
    internal static IEnumerable<(string Full, string Relative)> Walk(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                FileAttributes attrs;
                try
                {
                    attrs = File.GetAttributes(entry);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (attrs.HasFlag(FileAttributes.Directory))
                {
                    if (!attrs.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                yield return (entry, Path.GetRelativePath(root, entry).Replace('\\', '/'));
            }
        }
    }

    public static string? HashOf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
