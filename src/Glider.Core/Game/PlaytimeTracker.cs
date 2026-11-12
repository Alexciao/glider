namespace Glider.Core.Game;

/// <summary>
/// Turns "the game is running since X" into whole seconds to add to the persisted total. Pure (no clock, no I/O) so the
/// accumulation is unit-testable. Fractions of a second carry over between checkpoints, so periodic flushes never drift.
/// </summary>
internal sealed class PlaytimeTracker
{
    private DateTimeOffset? _from;

    public bool Active => _from is not null;

    public void Start(DateTimeOffset at) => _from ??= at;

    /// <summary>Whole seconds since the last checkpoint. <paramref name="final"/> ends the session. Never negative (clock steps back).</summary>
    public long Take(DateTimeOffset now, bool final = false)
    {
        if (_from is not { } from)
        {
            return 0;
        }

        var seconds = Math.Max(0, (long)Math.Floor((now - from).TotalSeconds));
        _from = final ? null : from.AddSeconds(seconds);
        return seconds;
    }

    public static GameRecord Apply(GameRecord record, long seconds, DateTimeOffset now) =>
        record with { PlaytimeSeconds = record.PlaytimeSeconds + seconds, LastPlayedAt = now };
}
