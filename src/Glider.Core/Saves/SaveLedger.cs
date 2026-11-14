using System.Collections.Immutable;
using Glider.Core.Infrastructure;

namespace Glider.Core.Saves;

/// <summary>
/// `state/saves.json`: per game, what the local save folder and the cloud looked like the last time they agreed. Written
/// atomically after every successful sync (and only then), so a crash never records a state that was not reached. Losing
/// it is safe: with no ledger the next sync is a "first sync", which asks instead of overwriting.
/// </summary>
internal sealed class SaveLedger : IDisposable
{
    private readonly JsonFileStore<SavesFile> _store;
    private readonly SemaphoreSlim _update = new(1, 1);
    private readonly ILog _log;
    private ImmutableDictionary<string, SaveLedgerEntry>? _current;

    public SaveLedger(AppPaths paths, ILog log)
    {
        _log = log;
        _store = new JsonFileStore<SavesFile>(Path.Combine(paths.State, "saves.json"), SaveJsonContext.Default.SavesFile, log);
    }

    public string FilePath => _store.FilePath;

    public async Task<SaveLedgerEntry?> GetAsync(string app, CancellationToken ct = default)
    {
        var map = await LoadAsync(ct).ConfigureAwait(false);
        return map.TryGetValue(app, out var e) ? e : null;
    }

    /// <summary>Synchronous view of what is loaded (null before the first load).</summary>
    public SaveLedgerEntry? Peek(string app) => _current is { } m && m.TryGetValue(app, out var e) ? e : null;

    public async Task SetAsync(string app, SaveLedgerEntry entry, CancellationToken ct = default)
    {
        await _update.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = (await LoadCoreAsync(ct).ConfigureAwait(false)).SetItem(app, entry);
            Volatile.Write(ref _current, map);
            await _store.SaveAsync(new SavesFile { Games = new Dictionary<string, SaveLedgerEntry>(map, StringComparer.Ordinal) }, ct).ConfigureAwait(false);
        }
        finally
        {
            _update.Release();
        }
    }

    public async Task<ImmutableDictionary<string, SaveLedgerEntry>> LoadAsync(CancellationToken ct = default)
    {
        if (_current is { } m)
        {
            return m;
        }

        await _update.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _update.Release();
        }
    }

    private async Task<ImmutableDictionary<string, SaveLedgerEntry>> LoadCoreAsync(CancellationToken ct)
    {
        if (_current is { } m)
        {
            return m;
        }

        var b = ImmutableDictionary.CreateBuilder<string, SaveLedgerEntry>(StringComparer.Ordinal);
        var file = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (file?.Games is not null)
        {
            foreach (var (app, e) in file.Games)
            {
                if (string.IsNullOrWhiteSpace(app) || e is null)
                {
                    continue;
                }

                b[app] = e with { Files = e.Files ?? [], Manifests = e.Manifests ?? [] };
            }
        }

        _log.Debug("saves", $"ledger loaded: {b.Count} game(s)");
        Volatile.Write(ref _current, b.ToImmutable());
        return _current!;
    }

    public void Dispose()
    {
        _update.Dispose();
        _store.Dispose();
    }
}
