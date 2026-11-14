using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Glider.Core.Infrastructure;

namespace Glider.Core.Game;

/// <summary>A prerequisite id Glider ran (the same id is the same redistributable in every game).</summary>
internal sealed record PrereqDone(DateTimeOffset At, string App, int ExitCode);

/// <summary>
/// state/prereqs.json: ids that have been installed (<see cref="Ids"/>) and ids the user chose to play without
/// (<see cref="Skipped"/>, so a failing installer does not nag at every launch; the explicit "install" button clears it).
/// </summary>
internal sealed record PrereqFile(int SchemaVersion, ImmutableDictionary<string, PrereqDone> Ids, ImmutableDictionary<string, DateTimeOffset> Skipped)
{
    public static PrereqFile Empty { get; } = new(2, ImmutableDictionary<string, PrereqDone>.Empty, ImmutableDictionary<string, DateTimeOffset>.Empty);
}

/// <summary>
/// state/thirdparty.json: which accounts were linked/activated with another store through Glider. Keys are
/// <c>ubisoft/&lt;accountId&gt;</c> and <c>ea/&lt;accountId&gt;/&lt;appName&gt;</c>; values are when.
/// </summary>
internal sealed record ThirdPartyFile(int SchemaVersion, ImmutableDictionary<string, DateTimeOffset> Activations)
{
    public static ThirdPartyFile Empty { get; } = new(2, ImmutableDictionary<string, DateTimeOffset>.Empty);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(PrereqFile))]
[JsonSerializable(typeof(ThirdPartyFile))]
internal sealed partial class GameStateJsonContext : JsonSerializerContext;

/// <summary>
/// One small immutable state file, loaded on first use and rewritten atomically on every change. Used by the
/// features that are not on the startup path (prerequisites, third-party links), so nothing is read at boot.
/// </summary>
internal sealed class LazyState<T> : IDisposable
    where T : class
{
    private readonly JsonFileStore<T> _store;
    private readonly T _empty;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private T? _value;

    public LazyState(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, T empty, ILog log)
    {
        _store = new JsonFileStore<T>(path, typeInfo, log);
        _empty = empty;
    }

    public T? Loaded => Volatile.Read(ref _value);

    public async Task<T> GetAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _value) is { } v)
        {
            return v;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _value ??= await _store.LoadAsync(ct).ConfigureAwait(false) ?? _empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Transforms the value and persists it. Returns the new value.</summary>
    public async Task<T> UpdateAsync(Func<T, T> change, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _value ??= await _store.LoadAsync(ct).ConfigureAwait(false) ?? _empty;
            var next = change(current);
            if (!ReferenceEquals(next, current))
            {
                await _store.SaveAsync(next, CancellationToken.None).ConfigureAwait(false);
                Volatile.Write(ref _value, next);
            }

            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _store.Dispose();
    }
}
