namespace Glider.Core.Infrastructure;

/// <summary>state/game.json: Glider's own record of the install. Null (file absent) = not installed.</summary>
public sealed class GameRecordStore : IGameRecordStore, IDisposable
{
    private readonly JsonFileStore<GameRecord> _store;
    private GameRecord? _current;

    private GameRecordStore(JsonFileStore<GameRecord> store, GameRecord? current)
    {
        _store = store;
        _current = current;
    }

    public GameRecord? Current => Volatile.Read(ref _current);

    public static string PathFor(AppPaths paths) => Path.Combine(paths.State, "game.json");

    public void Dispose() => _store.Dispose();

    public static async Task<GameRecordStore> LoadAsync(AppPaths paths, ILog log, CancellationToken ct = default)
    {
        var store = new JsonFileStore<GameRecord>(PathFor(paths), EngineJsonContext.Default.GameRecord, log);
        var record = await store.LoadAsync(ct).ConfigureAwait(false);
        if (record is not null && (string.IsNullOrWhiteSpace(record.InstallPath) || record.Version is null))
        {
            log.Warn("game", "game.json has no install path or version; treating as not installed.");
            record = null;
        }

        return new GameRecordStore(store, record);
    }

    public async Task SaveAsync(GameRecord? record)
    {
        await _store.SaveAsync(record).ConfigureAwait(false);
        Volatile.Write(ref _current, record);
    }
}
