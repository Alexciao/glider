using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Glider.Core.Infrastructure;

/// <summary>
/// One JSON document on disk, written atomically (see <see cref="AtomicFile"/>) with source-generated metadata.
/// Saves are ordered and coalesced: concurrent callers never interleave, and if several snapshots are queued
/// only the newest is written. Pass immutable snapshots.
/// </summary>
public sealed class JsonFileStore<T> : IDisposable
    where T : class
{
    /// <summary>Largest file <see cref="LoadAsync"/> will read (state files are a few KB).</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly ILog? _log;
    private readonly bool _keepBackup;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _gate = new();
    private T? _pending;
    private long _submitted;
    private long _written;

    public JsonFileStore(string path, JsonTypeInfo<T> typeInfo, ILog? log = null, bool keepBackup = true)
    {
        FilePath = Path.GetFullPath(path);
        _typeInfo = typeInfo;
        _log = log;
        _keepBackup = keepBackup;
    }

    public string FilePath { get; }

    /// <summary>Reads the document; falls back to the backup when the main file is missing or corrupt. Null if neither is usable.</summary>
    public async Task<T?> LoadAsync(CancellationToken ct = default)
    {
        foreach (var candidate in new[] { FilePath, AtomicFile.BackupPathFor(FilePath) })
        {
            if (new FileInfo(candidate) is { Exists: true, Length: > MaxBytes } big)
            {
                // Glider's state files are a few KB; never allocate whatever a corrupt or planted file claims.
                _log?.Warn("store", $"{Path.GetFileName(candidate)} is {big.Length} bytes, far too large; ignoring it.");
                continue;
            }

            var bytes = await AtomicFile.TryReadAllBytesAsync(candidate, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            try
            {
                var value = JsonSerializer.Deserialize(bytes, _typeInfo);
                if (value is not null)
                {
                    return value;
                }
            }
            catch (JsonException e)
            {
                _log?.Warn("store", $"{Path.GetFileName(candidate)} is unreadable, ignoring it: {e.Message}");
            }
        }

        return null;
    }

    public void Dispose() => _writeLock.Dispose();

    /// <summary>Raw bytes of the main file (for callers that need tolerant, field-by-field parsing).</summary>
    public Task<byte[]?> ReadRawAsync(CancellationToken ct = default) => AtomicFile.TryReadAllBytesAsync(FilePath, ct);

    /// <summary>Writes <paramref name="value"/> (or deletes the file for null). Completes once this or a newer snapshot is on disk.</summary>
    public async Task SaveAsync(T? value, CancellationToken ct = default)
    {
        long mine;
        lock (_gate)
        {
            _pending = value;
            mine = ++_submitted;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            T? snapshot;
            long version;
            lock (_gate)
            {
                if (_written >= mine)
                {
                    return; // a later save already wrote a newer snapshot
                }

                snapshot = _pending;
                version = _submitted;
            }

            if (snapshot is null)
            {
                AtomicFile.TryDelete(FilePath);
                AtomicFile.TryDelete(AtomicFile.BackupPathFor(FilePath));
            }
            else
            {
                var buffer = new ArrayBufferWriter<byte>(4096);
                using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
                {
                    JsonSerializer.Serialize(writer, snapshot, _typeInfo);
                }

                await AtomicFile.WriteAllBytesAsync(FilePath, buffer.WrittenMemory, _keepBackup, CancellationToken.None).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _written = version;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
