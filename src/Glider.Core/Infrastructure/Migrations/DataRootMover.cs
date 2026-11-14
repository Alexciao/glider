using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glider.Core.Overlay;

namespace Glider.Core.Infrastructure.Migrations;

/// <summary>
/// <c>data-root.json</c> in the data root: the commit marker of the schema-3 data-root move. Written only after every data
/// directory is in the new root; until it exists the old root is still the authoritative one.
/// </summary>
public sealed class DataRootMarker
{
    public int SchemaVersion { get; set; } = DataRoot.Schema;

    /// <summary>The root the data was moved from; null for a fresh install (or a marker re-created after it went missing).</summary>
    public string? MovedFrom { get; set; }

    public DateTimeOffset? MovedAt { get; set; }

    /// <summary>The data directories the new root held at commit time.</summary>
    public List<string> Directories { get; set; } = [];

    /// <summary>Directories that were copied (another volume) and whose verified source in the old root still has to be deleted.</summary>
    public List<string> PendingDeletes { get; set; } = [];

    /// <summary>All post-commit steps (path fix-ups, pointer, source deletes) are done.</summary>
    public bool Finished { get; set; }
}

/// <summary>
/// Where Glider's per-user data lives. Velopack installs Glider to <c>%LocalAppData%\Glider</c> and deletes that folder on
/// uninstall, so since schema 3 the data lives next to it in <c>%LocalAppData%\GliderData</c> (review H6, docs/v0.2-design.md §6).
/// </summary>
public static class DataRoot
{
    public const int Schema = 3;
    public const string FolderName = "GliderData";

    /// <summary>The pre-schema-3 data root, which is also Velopack's install root (packId Glider).</summary>
    public const string LegacyFolderName = "Glider";

    public const string MarkerFile = "data-root.json";

    /// <summary>Left in the old root after the move, for humans and support.</summary>
    public const string PointerFile = "data-moved-to.txt";

    /// <summary>Cross-volume copies are built here (inside the new root) and renamed into place only once verified.</summary>
    internal const string StagingFolder = ".moving";

    /// <summary>
    /// Every directory Glider keeps in its data root (AppPaths + the host's placeholder page), in move order: the ones another
    /// process may still hold open (a lingering WebView2 browser, an orphan Legendary, a game with the overlay loaded) come first,
    /// so a lock is usually found before anything else has moved. Anything else in the old root (Velopack's current\,
    /// packages\, Update.exe, Glider.exe, .portable…) is never touched.
    /// </summary>
    public static readonly ImmutableArray<string> Directories = ["webview2", "legendary", "overlay", "logs", "placeholder", "cache", "state", "backups"];

    /// <summary>Copies of these are verified by SHA-256 as well as by file list and sizes: they hold the irreplaceable data.</summary>
    internal static readonly ImmutableHashSet<string> HashVerified = ["state", "backups"];

    public static string LocalAppDataFolder => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string LegacyRootFor(string localAppData) => Path.Combine(localAppData, LegacyFolderName);

    public static string RootFor(string localAppData) => Path.Combine(localAppData, FolderName);

    public static DataRootMarker? ReadMarker(string root) => ReadMarker(root, DataRootFileSystem.Physical);

    internal static DataRootMarker? ReadMarker(string root, DataRootFileSystem fs)
    {
        try
        {
            var bytes = fs.ReadAllBytes(Path.Combine(root, MarkerFile));
            return bytes is { Length: > 0 } ? JsonSerializer.Deserialize(bytes, EngineJsonContext.Default.DataRootMarker) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // torn or unreadable = not committed
        }
    }

    internal static bool IsCommitted(DataRootMarker? marker) => marker is { SchemaVersion: >= Schema };

    /// <summary>
    /// Read-only: where the data is right now. The new root once the move committed; the old root while it still holds data that
    /// was never moved (the move runs at the next start); otherwise (fresh install) the new root.
    /// </summary>
    public static string Resolve(string legacyRoot, string root)
    {
        if (IsCommitted(ReadMarker(root)))
        {
            return root;
        }

        var hasData = Directories.Any(d => Directory.Exists(Path.Combine(legacyRoot, d)));
        return hasData && !File.Exists(Path.Combine(legacyRoot, PointerFile)) ? legacyRoot : root;
    }

    public static string ResolveForCurrentUser() => Resolve(LegacyRootFor(LocalAppDataFolder), RootFor(LocalAppDataFolder));

    /// <summary>
    /// The host's entry point, called with the single-instance mutex held and before anything opens a file in the data root.
    /// An explicit root (<c>--data-dir</c>, test runs, portable use) is used as it is and never moved.
    /// </summary>
    public static DataRootMoveResult Prepare(string? explicitRoot, DataRootMoverOptions? options = null, string? localAppData = null, DataRootFileSystem? fs = null)
    {
        if (explicitRoot is not null)
        {
            return new DataRootMoveResult(DataRootMoveStatus.NotNeeded, explicitRoot, []);
        }

        var lad = localAppData ?? LocalAppDataFolder;
        return new DataRootMover(LegacyRootFor(lad), RootFor(lad), options, fs).Run();
    }
}

public enum DataRootMoveStatus
{
    /// <summary>An explicit root: nothing to do.</summary>
    NotNeeded,
    /// <summary>The new root was already committed (post-commit steps finished if an earlier run was interrupted).</summary>
    AlreadyMoved,
    /// <summary>Nothing to move: the new root was created and committed.</summary>
    Fresh,
    /// <summary>The data was moved (or an interrupted move was completed) and committed.</summary>
    Moved,
    /// <summary>A directory could not be moved (locked): everything was put back, this session uses the old root, retried next start.</summary>
    Deferred,
    /// <summary>The move failed and could not be put back: the data is split between both roots, so Glider must not start (next start resumes).</summary>
    Failed,
}

public sealed record DataRootMoveResult(DataRootMoveStatus Status, string Root, IReadOnlyList<string> Messages, Exception? Error = null)
{
    public bool CanStart => Status != DataRootMoveStatus.Failed;
}

public sealed class DataRootMoverOptions
{
    /// <summary>Waits between attempts to rename a directory another process holds open (about 1.9 s in total by default).</summary>
    public IReadOnlyList<TimeSpan> LockRetryDelays { get; init; } = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)];

    /// <summary>False: renames only (a cross-volume directory defers the move). The uninstall hook has no time for a copy.</summary>
    public bool AllowCopy { get; init; } = true;

    /// <summary>When set, an HKCU overlay registration pointing at the old root's overlay folder is re-pointed at the new one.</summary>
    public IOverlayRegistry? OverlayRegistry { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Test hook, called after every completed step; throwing <see cref="SimulatedCrashException"/> simulates the process dying there.</summary>
    internal Action<string>? Step { get; init; }
}

/// <summary>Thrown by test step hooks: propagates out of the mover untouched (no rollback), like a process kill would.</summary>
internal sealed class SimulatedCrashException(string step) : Exception($"simulated crash after '{step}'");

/// <summary>A copied directory does not match its source.</summary>
internal sealed class DataRootVerifyException(string message) : IOException(message);

/// <summary>The file-system operations of the move. Tests subclass it to simulate another volume or a failing operation.</summary>
public class DataRootFileSystem
{
    public static DataRootFileSystem Physical { get; } = new();

    private static readonly EnumerationOptions All = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        MatchType = MatchType.Simple,
    };

    public virtual bool DirectoryExists(string path) => Directory.Exists(path);

    public virtual bool FileExists(string path) => File.Exists(path);

    public virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <summary>A rename: atomic for the whole tree, and refused while any file inside is open. Throws on another volume.</summary>
    public virtual void MoveDirectory(string source, string target) => Directory.Move(source, target);

    public virtual void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    public virtual IEnumerable<string> EnumerateFiles(string dir) => Directory.EnumerateFiles(dir, "*", All);

    public virtual IEnumerable<string> EnumerateDirectories(string dir) => Directory.EnumerateDirectories(dir, "*", All);

    public virtual long Length(string path) => new FileInfo(path).Length;

    /// <summary>Opened denying writers: a file another process is writing makes the copy fail instead of copying a torn file.</summary>
    public virtual Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);

    public virtual Stream Create(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);

    public virtual void CopyTimestamps(string source, string target) => File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));

    public virtual byte[]? ReadAllBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    public virtual void WriteAllBytesAtomic(string path, byte[] content) => AtomicFile.WriteAllBytesAsync(path, content).GetAwaiter().GetResult();

    public virtual void Sleep(TimeSpan delay) => Thread.Sleep(delay);

    /// <summary>ERROR_NOT_SAME_DEVICE, or the framework's own "same root" check.</summary>
    public static bool IsCrossVolume(Exception e) => e is IOException { HResult: unchecked((int)0x80070011) };
}

/// <summary>
/// The one-time, crash-safe schema-3 move of Glider's data out of Velopack's install root (docs/v0.2-design.md §6).
/// <para>Invariants that make every interruption resumable from the file system alone:</para>
/// <list type="bullet">
/// <item>Before the marker, a data directory is in the new root only because this mover put it there: by a rename (atomic,
/// refused while a file inside is open, so never partial), or by renaming a fully copied and verified staging copy into place.
/// So "only in the new root" = moved; "in both" = verified copy whose source is deleted after commit; anything in
/// <c>.moving\</c> = an unfinished copy, discarded.</item>
/// <item>A copied source is deleted only after the marker is written.</item>
/// <item>If any directory can't be moved, every directory is put back (renamed back, or the copy discarded) and the session
/// runs on the old root; if that fails too the result is <see cref="DataRootMoveStatus.Failed"/> and the host must not start,
/// so Glider never runs on a half-moved root.</item>
/// <item>Post-commit steps (path fix-ups, overlay registration, pointer, source deletes) are idempotent and repeated until the
/// marker says <see cref="DataRootMarker.Finished"/>.</item>
/// </list>
/// </summary>
public sealed class DataRootMover
{
    private readonly string _legacy;
    private readonly string _root;
    private readonly DataRootMoverOptions _options;
    private readonly DataRootFileSystem _fs;
    private readonly List<string> _messages = [];

    public DataRootMover(string legacyRoot, string root, DataRootMoverOptions? options = null, DataRootFileSystem? fs = null)
    {
        _legacy = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _options = options ?? new DataRootMoverOptions();
        _fs = fs ?? DataRootFileSystem.Physical;
        if (string.Equals(_legacy, _root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The old and the new data root must differ.", nameof(root));
        }
    }

    private string Old(string dir) => Path.Combine(_legacy, dir);

    private string New(string dir) => Path.Combine(_root, dir);

    private string Staging => Path.Combine(_root, DataRoot.StagingFolder);

    public DataRootMoveResult Run()
    {
        var marker = DataRoot.ReadMarker(_root, _fs);
        if (DataRoot.IsCommitted(marker))
        {
            FinishCommitted(marker!);
            return Result(DataRootMoveStatus.AlreadyMoved, _root);
        }

        var pointer = Path.Combine(_legacy, DataRoot.PointerFile);
        var legacyDirs = DataRoot.Directories.Where(d => _fs.DirectoryExists(Old(d))).ToList();
        if (legacyDirs.Count == 0 || _fs.FileExists(pointer))
        {
            if (legacyDirs.Count > 0)
            {
                // The pointer is written only after the marker, so the marker was removed by hand: don't move older leftovers over.
                Note($"{DataRoot.PointerFile} exists in {_legacy} but {DataRoot.MarkerFile} is missing in {_root}; starting with the new root and leaving the old folders alone.");
            }

            // No old data to move: a fresh install, or a move whose last directory went across just before a crash.
            var fresh = legacyDirs.Count > 0 || !DataRoot.Directories.Any(d => _fs.DirectoryExists(New(d)));
            var status = fresh ? DataRootMoveStatus.Fresh : DataRootMoveStatus.Moved;
            DataRootMarker committed;
            try
            {
                CleanStaging(bestEffort: true);
                committed = Commit(fresh ? null : _legacy, []);
                Step("marker");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Everything that matters is in the new root already, so it is used anyway; the marker is retried next start.
                Note($"Writing {DataRoot.MarkerFile} failed: {e.Message}; retried next start.");
                return Result(status, _root, e);
            }

            FinishCommitted(committed);

            return Result(status, _root);
        }

        var copied = new List<string>();
        DataRootMarker commit;
        try
        {
            _fs.CreateDirectory(_root);
            CleanStaging();
            foreach (var dir in DataRoot.Directories)
            {
                if (!_fs.DirectoryExists(Old(dir)))
                {
                    continue; // moved by an earlier, interrupted run (or never existed)
                }

                if (_fs.DirectoryExists(New(dir)))
                {
                    if (IsCompleteCopy(dir))
                    {
                        copied.Add(dir); // a verified copy from an earlier run: its source goes after the commit
                        continue;
                    }

                    // Not a copy of the old folder (e.g. created by something else before the first move): keep it, renamed,
                    // and move the real data in. The old source is never scheduled for deletion against an unverified copy.
                    SetAside(dir);
                }

                if (MoveOne(dir))
                {
                    copied.Add(dir);
                }

                Step("move:" + dir);
            }

            CleanStaging(bestEffort: true); // the copies were renamed out of it: only the empty folder is left
            commit = Commit(_legacy, copied);
            Step("marker");
        }
        catch (Exception e) when (e is not SimulatedCrashException and not OperationCanceledException)
        {
            Note($"Moving the data to {_root} did not complete ({e.GetType().Name}: {e.Message}); putting everything back in {_legacy}.");
            return Rollback(e);
        }

        Note($"Moved Glider's data from {_legacy} to {_root}" + (copied.Count > 0 ? $" (copied and verified: {string.Join(", ", copied)})." : "."));
        FinishCommitted(commit);
        return Result(DataRootMoveStatus.Moved, _root);
    }

    // ------------------------------------------------------------------ forward

    /// <summary>Moves one directory; returns true when it had to be copied (another volume).</summary>
    private bool MoveOne(string dir)
    {
        var crossRoot = !string.Equals(Path.GetPathRoot(_legacy), Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (crossRoot)
                {
                    throw new IOException("different volume", unchecked((int)0x80070011));
                }

                _fs.MoveDirectory(Old(dir), New(dir));
                return false;
            }
            catch (IOException e) when (DataRootFileSystem.IsCrossVolume(e))
            {
                if (!_options.AllowCopy)
                {
                    throw new IOException($"{dir} is on another volume and copying is not allowed here.", e);
                }

                CopyVerifyCommit(dir);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < _options.LockRetryDelays.Count)
            {
                // Most likely in use: a WebView2 browser or Legendary process from the previous session still exiting.
                _fs.Sleep(_options.LockRetryDelays[attempt]);
            }
        }
    }

    private void CopyVerifyCommit(string dir)
    {
        var stage = Path.Combine(Staging, dir);
        if (_fs.DirectoryExists(stage))
        {
            _fs.DeleteDirectory(stage);
        }

        var source = Old(dir);
        _fs.CreateDirectory(stage);
        foreach (var sub in _fs.EnumerateDirectories(source))
        {
            _fs.CreateDirectory(Path.Combine(stage, Path.GetRelativePath(source, sub)));
        }

        foreach (var file in _fs.EnumerateFiles(source))
        {
            var target = Path.Combine(stage, Path.GetRelativePath(source, file));
            using (var input = _fs.OpenRead(file))
            using (var output = _fs.Create(target))
            {
                input.CopyTo(output);
                if (output is FileStream fsOut)
                {
                    fsOut.Flush(flushToDisk: true); // the source is deleted later: the copy must be on disk, not in the cache
                }
            }

            _fs.CopyTimestamps(file, target);
        }

        Step("copy:" + dir);
        Verify(source, stage, DataRoot.HashVerified.Contains(dir));
        Step("verify:" + dir);
        _fs.MoveDirectory(stage, New(dir)); // same volume (both in the new root): the verified copy appears atomically
    }

    /// <summary>True when the new root's folder is a complete, byte-identical copy of the old one (every file hashed).</summary>
    private bool IsCompleteCopy(string dir)
    {
        try
        {
            Verify(Old(dir), New(dir), hashes: true);
            return true;
        }
        catch (DataRootVerifyException)
        {
            return false;
        }
    }

    /// <summary>Renames a pre-existing, unrelated folder in the new root out of the way (never deletes it).</summary>
    private void SetAside(string dir)
    {
        var stamp = _options.Time.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = New(dir) + ".preexisting-" + stamp;
        for (var n = 2; _fs.DirectoryExists(target); n++)
        {
            target = New(dir) + ".preexisting-" + stamp + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        _fs.MoveDirectory(New(dir), target);
        Note($"{New(dir)} existed but was not a copy of {Old(dir)}; kept it as {Path.GetFileName(target)} and moved the old folder in.");
    }

    private void Verify(string source, string copy, bool hashes)
    {
        var a = Listing(source);
        var b = Listing(copy);
        if (!a.Directories.SetEquals(b.Directories))
        {
            throw new DataRootVerifyException($"The copy of {source} has different folders.");
        }

        if (a.Files.Count != b.Files.Count)
        {
            throw new DataRootVerifyException($"The copy of {source} has {b.Files.Count} files instead of {a.Files.Count}.");
        }

        foreach (var (rel, length) in a.Files)
        {
            if (!b.Files.TryGetValue(rel, out var copied) || copied != length)
            {
                throw new DataRootVerifyException($"The copy of {Path.Combine(source, rel)} is missing or has the wrong size.");
            }

            if (hashes && !Hash(Path.Combine(source, rel)).AsSpan().SequenceEqual(Hash(Path.Combine(copy, rel))))
            {
                throw new DataRootVerifyException($"The copy of {Path.Combine(source, rel)} differs from the original.");
            }
        }
    }

    private (Dictionary<string, long> Files, HashSet<string> Directories) Listing(string dir)
    {
        var files = _fs.EnumerateFiles(dir).ToDictionary(f => Path.GetRelativePath(dir, f), _fs.Length, StringComparer.OrdinalIgnoreCase);
        var dirs = _fs.EnumerateDirectories(dir).Select(d => Path.GetRelativePath(dir, d)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (files, dirs);
    }

    private byte[] Hash(string path)
    {
        using var s = _fs.OpenRead(path);
        return SHA256.HashData(s);
    }

    private DataRootMarker Commit(string? movedFrom, List<string> pendingDeletes)
    {
        _fs.CreateDirectory(_root);
        var marker = new DataRootMarker
        {
            SchemaVersion = DataRoot.Schema,
            MovedFrom = movedFrom,
            MovedAt = _options.Time.GetUtcNow(),
            Directories = [.. DataRoot.Directories.Where(d => _fs.DirectoryExists(New(d)))],
            PendingDeletes = pendingDeletes,
            Finished = movedFrom is null,
        };
        WriteMarker(marker);
        return marker;
    }

    private void WriteMarker(DataRootMarker marker) =>
        _fs.WriteAllBytesAtomic(Path.Combine(_root, DataRoot.MarkerFile), JsonSerializer.SerializeToUtf8Bytes(marker, EngineJsonContext.Default.DataRootMarker));

    // ------------------------------------------------------------------ rollback

    private DataRootMoveResult Rollback(Exception cause)
    {
        var failures = new List<string>();
        var n = 0;
        foreach (var dir in DataRoot.Directories.Reverse())
        {
            var moved = New(dir);
            if (!_fs.DirectoryExists(moved))
            {
                continue;
            }

            try
            {
                if (_fs.DirectoryExists(Old(dir)))
                {
                    // A copy: the source is intact and is what this session will use; park the copy for deletion.
                    _fs.CreateDirectory(Staging);
                    _fs.MoveDirectory(moved, Path.Combine(Staging, $"discard-{dir}-{n++}"));
                }
                else
                {
                    RenameWithRetries(moved, Old(dir));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{dir}: {e.Message}");
            }
        }

        CleanStaging(bestEffort: true);
        if (failures.Count > 0)
        {
            Note("Putting the data back failed for " + string.Join("; ", failures) + ". Glider will not start on a half-moved data folder; the move resumes at the next start.");
            return Result(DataRootMoveStatus.Failed, _root, cause);
        }

        Note($"This session uses {_legacy}; the move is retried at the next start.");
        return Result(DataRootMoveStatus.Deferred, _legacy, cause);
    }

    private void RenameWithRetries(string source, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _fs.MoveDirectory(source, target);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < _options.LockRetryDelays.Count)
            {
                _fs.Sleep(_options.LockRetryDelays[attempt]);
            }
        }
    }

    private void CleanStaging(bool bestEffort = false)
    {
        try
        {
            if (_fs.DirectoryExists(Staging))
            {
                _fs.DeleteDirectory(Staging); // unfinished or discarded copies only: every source is still in the old root
            }
        }
        catch (Exception e) when (bestEffort && e is IOException or UnauthorizedAccessException)
        {
            Note($"Could not delete {Staging} yet: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ post-commit

    private void FinishCommitted(DataRootMarker marker)
    {
        try
        {
            if (marker.MovedFrom is not { } from)
            {
                return;
            }

            if (marker.Finished)
            {
                EnsurePointer(from); // e.g. a reinstall recreated the old root
                return;
            }

            FixPaths(from);
            Step("fixups");
            RepointOverlay(from);
            EnsurePointer(from);
            Step("pointer");
            foreach (var dir in marker.PendingDeletes.ToList())
            {
                var source = Path.Combine(from, dir);
                try
                {
                    if (_fs.DirectoryExists(source))
                    {
                        _fs.DeleteDirectory(source);
                    }

                    marker.PendingDeletes.Remove(dir);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Note($"Could not delete the old copy {source} yet ({e.Message}); retried next start.");
                }

                Step("delete:" + dir);
            }

            marker.Finished = marker.PendingDeletes.Count == 0;
            WriteMarker(marker);
            Step("finished");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Committed already: the session uses the new root either way; what's left is retried next start.
            Note($"Finishing the data move failed ({e.Message}); retried next start.");
        }
    }

    private void EnsurePointer(string from)
    {
        var pointer = Path.Combine(from, DataRoot.PointerFile);
        if (!_fs.DirectoryExists(from) || _fs.FileExists(pointer))
        {
            return;
        }

        var text = string.Create(CultureInfo.InvariantCulture, $"""
            Glider's data (Epic sign-in, library, settings, save and EGL backups, logs) has moved to:
            {_root}

            This folder ({from}) now holds only the Glider app itself, which Velopack replaces on update and deletes on uninstall.
            Your data stays in the folder above. Moved {_options.Time.GetUtcNow():yyyy-MM-dd HH:mm} UTC.

            """);
        _fs.WriteAllBytesAtomic(pointer, Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n")));
    }

    /// <summary>
    /// Re-points the few absolute paths into the old root that Glider's data carries: Legendary's <c>overlay_install.json</c>
    /// (the managed overlay lives in <c>overlay\</c>) and job targets in <c>state\jobs.json</c> (an overlay install in progress).
    /// Idempotent: once rewritten nothing matches any more.
    /// </summary>
    private void FixPaths(string from)
    {
        foreach (var file in new[] { Path.Combine(_root, "legendary", OverlayFiles.InstallJson("")), Path.Combine(_root, "state", "jobs.json") })
        {
            var bytes = _fs.ReadAllBytes(file);
            if (bytes is null)
            {
                continue;
            }

            JsonNode? doc;
            try
            {
                var span = bytes.AsSpan();
                if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
                {
                    span = span[3..];
                }

                doc = JsonNode.Parse(span, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch (JsonException)
            {
                Note($"{file} is not readable JSON; its paths were left as they are.");
                continue;
            }

            if (doc is not null && Rewrite(doc, from))
            {
                _fs.WriteAllBytesAtomic(file, Encoding.UTF8.GetBytes(doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
                Note($"Re-pointed paths in {file} to {_root}.");
            }
        }
    }

    private bool Rewrite(JsonNode node, string from)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var s) && Repoint(s, from) is { } p)
                    {
                        o[key] = p;
                        changed = true;
                    }
                    else if (value is not null)
                    {
                        changed |= Rewrite(value, from);
                    }
                }

                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && Repoint(s, from) is { } p)
                    {
                        a[i] = p;
                        changed = true;
                    }
                    else if (a[i] is { } child)
                    {
                        changed |= Rewrite(child, from);
                    }
                }

                break;
        }

        return changed;
    }

    /// <summary>The same path under the new root when <paramref name="value"/> is a path inside the old root's data folders.</summary>
    internal string? Repoint(string value, string from)
    {
        var normalized = value.Replace('/', '\\');
        if (!normalized.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = normalized[(from.Length + 1)..];
        var first = rest.Split('\\', 2)[0];
        return DataRoot.Directories.Contains(first, StringComparer.OrdinalIgnoreCase) ? Path.Combine(_root, rest) : null;
    }

    /// <summary>HKCU's EOS overlay registration follows the overlay folder (it would otherwise point at a folder that no longer exists).</summary>
    private void RepointOverlay(string from)
    {
        if (_options.OverlayRegistry is not { } registry)
        {
            return;
        }

        try
        {
            var current = registry.GetOverlayPath();
            var target = New("overlay");
            if (current is null || Repoint(current, from) is not { } moved || !string.Equals(moved, target, StringComparison.OrdinalIgnoreCase) ||
                !OverlayFiles.IsOverlayFolder(target))
            {
                return;
            }

            registry.Disable(); // drops the old Vulkan layer values too
            registry.Enable(target);
            Note($"Re-pointed the EOS overlay registration to {target}.");
        }
        catch (Exception e) when (e is not SimulatedCrashException)
        {
            Note($"Re-pointing the EOS overlay registration failed: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ helpers

    private void Step(string name) => _options.Step?.Invoke(name);

    private void Note(string message) => _messages.Add(message);

    private DataRootMoveResult Result(DataRootMoveStatus status, string root, Exception? error = null) => new(status, root, [.. _messages], error);
}
