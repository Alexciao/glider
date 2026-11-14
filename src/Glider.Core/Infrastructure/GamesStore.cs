using System.Collections.Immutable;
using Glider.Core.Contracts;

namespace Glider.Core.Infrastructure;

/// <summary>state/games.json (schema 2): Glider's own per-game data, keyed by AppName.</summary>
internal sealed class GamesFile
{
    public int SchemaVersion { get; set; } = 2;

    public Dictionary<string, GameEntry> Games { get; set; } = [];
}

/// <summary>
/// Immutable map of <see cref="GameEntry"/> per app, persisted atomically (ordered, coalesced saves with a .bak).
/// Updates are serialized so concurrent read-modify-writes of different games never lose each other.
/// </summary>
public sealed class GamesStore : IGamesStore, IDisposable
{
    private readonly JsonFileStore<GamesFile> _store;
    private readonly SemaphoreSlim _update = new(1, 1);
    private ImmutableDictionary<string, GameEntry> _current;

    private GamesStore(JsonFileStore<GamesFile> store, ImmutableDictionary<string, GameEntry> current)
    {
        _store = store;
        _current = current;
    }

    public static string PathFor(AppPaths paths) => Path.Combine(paths.State, "games.json");

    public ImmutableDictionary<string, GameEntry> Current => Volatile.Read(ref _current);

    public event Action? Changed;

    public GameEntry Get(string appName) => Current.TryGetValue(appName, out var e) ? e : GameEntry.Empty;

    public static async Task<GamesStore> LoadAsync(AppPaths paths, ILog log, CancellationToken ct = default)
    {
        var store = new JsonFileStore<GamesFile>(PathFor(paths), EngineJsonContext.Default.GamesFile, log);
        var file = await store.LoadAsync(ct).ConfigureAwait(false);
        return new GamesStore(store, Normalize(file, log));
    }

    internal static ImmutableDictionary<string, GameEntry> Normalize(GamesFile? file, ILog? log)
    {
        var b = ImmutableDictionary.CreateBuilder<string, GameEntry>(StringComparer.Ordinal);
        if (file?.Games is null)
        {
            return b.ToImmutable();
        }

        foreach (var (app, raw) in file.Games)
        {
            if (string.IsNullOrWhiteSpace(app) || raw is null)
            {
                continue;
            }

            var install = raw.Install;
            if (install is not null && (string.IsNullOrWhiteSpace(install.InstallPath) || install.Version is null))
            {
                log?.Warn("games", $"games.json: install record of {app} has no path or version; dropping it.");
                install = null;
            }
            else if (install is not null && install.AppName != app)
            {
                install = install with { AppName = app };
            }

            b[app] = raw with
            {
                Install = install,
                PlaytimeSeconds = Math.Max(0, raw.PlaytimeSeconds),
                Settings = raw.Settings ?? GameSettings.Default,
                Shortcuts = raw.Shortcuts ?? new ShortcutState(false, false),
            };
        }

        return b.ToImmutable();
    }

    public async Task<GameEntry> UpdateAsync(string appName, Func<GameEntry, GameEntry> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        GameEntry next;
        await _update.WaitAsync().ConfigureAwait(false);
        try
        {
            var map = Current;
            var before = map.TryGetValue(appName, out var e) ? e : GameEntry.Empty;
            next = change(before);
            if (next == before)
            {
                return next;
            }

            map = next.IsEmpty ? map.Remove(appName) : map.SetItem(appName, next);
            Volatile.Write(ref _current, map);
            await _store.SaveAsync(new GamesFile { Games = new Dictionary<string, GameEntry>(map, StringComparer.Ordinal) }).ConfigureAwait(false);
        }
        finally
        {
            _update.Release();
        }

        Changed?.Invoke();
        return next;
    }

    public void Dispose()
    {
        _update.Dispose();
        _store.Dispose();
    }
}

/// <summary>state/collections.json (schema 2).</summary>
internal sealed class CollectionsFile
{
    public int SchemaVersion { get; set; } = 2;

    public List<Collection> Collections { get; set; } = [];
}

/// <summary>User collections (name + member AppNames), persisted atomically.</summary>
public sealed class CollectionsStore : IDisposable
{
    public const int MaxNameLength = 64;
    public const int MaxCollections = 200;

    private readonly JsonFileStore<CollectionsFile> _store;
    private readonly SemaphoreSlim _update = new(1, 1);
    private ImmutableList<Collection> _current;

    private CollectionsStore(JsonFileStore<CollectionsFile> store, ImmutableList<Collection> current)
    {
        _store = store;
        _current = current;
    }

    public static string PathFor(AppPaths paths) => Path.Combine(paths.State, "collections.json");

    public IReadOnlyList<Collection> Current => Volatile.Read(ref _current);

    public event Action? Changed;

    /// <summary>A store with no collections yet (first run, tests); writes go to collections.json.</summary>
    public static CollectionsStore Empty(AppPaths paths, ILog? log = null) =>
        new(new JsonFileStore<CollectionsFile>(PathFor(paths), EngineJsonContext.Default.CollectionsFile, log), ImmutableList<Collection>.Empty);

    public static async Task<CollectionsStore> LoadAsync(AppPaths paths, ILog log, CancellationToken ct = default)
    {
        var store = new JsonFileStore<CollectionsFile>(PathFor(paths), EngineJsonContext.Default.CollectionsFile, log);
        var file = await store.LoadAsync(ct).ConfigureAwait(false);
        var list = (file?.Collections ?? [])
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id) && c.Name is not null)
            .Select(c => c with { AppNames = (c.AppNames ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal).ToList() })
            .GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.First())
            .ToImmutableList();
        return new CollectionsStore(store, list);
    }

    public Task<Collection> CreateAsync(string name) => MutateAsync(list =>
    {
        var clean = CleanName(name);
        if (list.Count >= MaxCollections)
        {
            throw new GliderException(Errors.InvalidRequest($"At most {MaxCollections} collections"));
        }

        var c = new Collection(Guid.NewGuid().ToString("N")[..12], clean, []);
        return (list.Add(c), c);
    });

    public Task<Collection> RenameAsync(string id, string name) => MutateAsync(list =>
    {
        var (i, c) = Find(list, id);
        var renamed = c with { Name = CleanName(name) };
        return (list.SetItem(i, renamed), renamed);
    });

    public Task DeleteAsync(string id) => MutateAsync(list =>
    {
        var (i, c) = Find(list, id);
        return (list.RemoveAt(i), c);
    });

    public Task<Collection> SetMembershipAsync(string id, string appName, bool member) => MutateAsync(list =>
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            throw new GliderException(Errors.InvalidRequest("appName is required"));
        }

        var (i, c) = Find(list, id);
        var apps = c.AppNames.Where(a => a != appName).ToList();
        if (member)
        {
            apps.Add(appName);
        }

        var next = c with { AppNames = apps };
        return (list.SetItem(i, next), next);
    });

    private static (int Index, Collection Item) Find(ImmutableList<Collection> list, string id)
    {
        var i = list.FindIndex(c => c.Id == id);
        return i < 0 ? throw new GliderException(Errors.InvalidRequest($"Unknown collection '{id}'")) : (i, list[i]);
    }

    private static string CleanName(string? name)
    {
        var n = (name ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (n.Length is 0 or > MaxNameLength)
        {
            throw new GliderException(Errors.InvalidRequest($"Collection names are 1–{MaxNameLength} characters"));
        }

        return n;
    }

    private async Task<Collection> MutateAsync(Func<ImmutableList<Collection>, (ImmutableList<Collection> List, Collection Result)> change)
    {
        Collection result;
        await _update.WaitAsync().ConfigureAwait(false);
        try
        {
            var (list, r) = change(_current);
            result = r;
            Volatile.Write(ref _current, list);
            await _store.SaveAsync(new CollectionsFile { Collections = [.. list] }).ConfigureAwait(false);
        }
        finally
        {
            _update.Release();
        }

        Changed?.Invoke();
        return result;
    }

    public void Dispose()
    {
        _update.Dispose();
        _store.Dispose();
    }
}

/// <summary>state/onboarding.json (schema 2): <c>{schemaVersion, completed, step, skipped}</c>.</summary>
internal sealed class OnboardingFile
{
    public int SchemaVersion { get; set; } = 2;

    public bool Completed { get; set; }

    public OnboardingStep Step { get; set; } = OnboardingStep.Welcome;

    public List<OnboardingStep> Skipped { get; set; } = [];
}

/// <summary>Persists the onboarding step so the flow resumes after a restart. Kept out of Settings so "reset settings" doesn't replay it.</summary>
public interface IOnboardingStore
{
    OnboardingState Current { get; }

    Task<OnboardingState> SetAsync(OnboardingState state);
}

public sealed class FileOnboardingStore : IOnboardingStore, IDisposable
{
    private readonly JsonFileStore<OnboardingFile> _store;
    private OnboardingState _current;

    private FileOnboardingStore(JsonFileStore<OnboardingFile> store, OnboardingState current)
    {
        _store = store;
        _current = current;
    }

    public static string PathFor(AppPaths paths) => Path.Combine(paths.State, "onboarding.json");

    public OnboardingState Current => Volatile.Read(ref _current);

    /// <summary>Not started yet (first run, tests); writes go to onboarding.json.</summary>
    public static FileOnboardingStore Empty(AppPaths paths, ILog? log = null) =>
        new(new JsonFileStore<OnboardingFile>(PathFor(paths), EngineJsonContext.Default.OnboardingFile, log), new OnboardingState(false, OnboardingStep.Welcome, []));

    public static async Task<FileOnboardingStore> LoadAsync(AppPaths paths, ILog log, CancellationToken ct = default)
    {
        var store = new JsonFileStore<OnboardingFile>(PathFor(paths), EngineJsonContext.Default.OnboardingFile, log);
        OnboardingFile? file = null;
        try
        {
            file = await store.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            log.Warn("onboarding", "onboarding.json is unreadable; starting over.", e);
        }

        var state = file is null
            ? new OnboardingState(false, OnboardingStep.Welcome, [])
            : new OnboardingState(file.Completed, file.Completed ? OnboardingStep.Done : Enum.IsDefined(file.Step) ? file.Step : OnboardingStep.Welcome,
                (file.Skipped ?? []).Where(Enum.IsDefined).Distinct().ToList());
        return new FileOnboardingStore(store, state);
    }

    public async Task<OnboardingState> SetAsync(OnboardingState state)
    {
        Volatile.Write(ref _current, state);
        await _store.SaveAsync(new OnboardingFile { Completed = state.Completed, Step = state.Step, Skipped = [.. state.Skipped] }).ConfigureAwait(false);
        return state;
    }

    public void Dispose() => _store.Dispose();
}
