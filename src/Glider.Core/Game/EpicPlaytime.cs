using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Glider.Core.Legendary;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// Epic's own playtime record (what the Epic Games Launcher shows as "You've played"), imported per game so Glider's figure includes
/// play from before Glider. One GET to Epic's library service (the call Playnite's Epic integration makes) with the session Legendary
/// already holds: user.json is only read, the access token stays in memory and is never logged, and Glider never refreshes it
/// itself (Legendary does that during pre-warm; an expired or refused token just means trying again later).
/// </summary>
internal sealed class EpicPlaytimeService : IDisposable
{
    /// <summary>Every playtime record of one account, in one answer.</summary>
    internal static Uri EndpointFor(string accountId) =>
        new($"https://library-service.live.use1a.on.epicgames.com/library/api/public/playtime/account/{Uri.EscapeDataString(accountId)}/all");

    /// <summary>Epic's figure moves only when someone plays; once an hour is plenty.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>After a game closes, Epic may need a moment to post the session.</summary>
    internal static readonly TimeSpan AfterSessionDelay = TimeSpan.FromMinutes(2);

    /// <summary>How long after the last Glider session Epic may still post it. Past this, Glider play its record didn't absorb is Glider's own.</summary>
    internal static readonly TimeSpan SettleAfter = TimeSpan.FromHours(6);

    /// <summary>Epic's record must grow by at least this share of the pending Glider play to count as having recorded it (Epic rounds sessions).</summary>
    internal const double AbsorbedShare = 0.8;

    private readonly ServiceContext _c;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _soon;
    private bool _disposed;

    public EpicPlaytimeService(ServiceContext context, HttpMessageHandler? handler = null)
    {
        _c = context;
        _http = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false, UseProxy = true }, disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Glider"); // generic: nothing about the user or the machine
    }

    public void Dispose()
    {
        _disposed = true;
        _soon?.Dispose();
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>Background loop, started after pre-warm (Legendary has just refreshed the session): import now, then hourly.</summary>
    public async Task RunPeriodicAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(ct).ConfigureAwait(false);
            await Task.Delay(Interval, _c.Time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A game closed: import again shortly, so a session Epic recorded shows up without waiting for the hourly pass.</summary>
    public void RefreshSoon()
    {
        if (_disposed)
        {
            return;
        }

        _soon ??= _c.Time.CreateTimer(_ => _ = RefreshAsync(_c.Shutdown), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _soon.Change(AfterSessionDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>One import. Returns how many games changed, or null when nothing could be read (signed out, offline, refused). Never throws.</summary>
    public async Task<int?> RefreshAsync(CancellationToken ct)
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }

        try
        {
            var session = await LegendaryFiles.ReadSessionAsync(LegendaryFiles.UserJson(_c.Paths.LegendaryConfig), ct).ConfigureAwait(false);
            if (session is null || session.ExpiresAt <= _c.Now.AddMinutes(1))
            {
                return null; // signed out, or Legendary hasn't refreshed the session yet: the next pass tries again
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, EndpointFor(session.AccountId));
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", session.AccessToken);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _c.Log.Info("playtime", $"Epic did not accept the session ({(int)response.StatusCode}); trying again later");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _c.Log.Warn("playtime", $"Epic's playtime service answered {(int)response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var epic = Parse(body);
            return epic is null ? null : await ApplyAsync(epic).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            _c.Log.Info("playtime", $"Epic's playtime service is unreachable ({e.GetType().Name}); trying again later");
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _c.Log.Warn("playtime", "playtime import failed", e);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stores Epic's figure for the games in this library (Epic also lists engines and tools). Epic's totals only grow.</summary>
    private async Task<int> ApplyAsync(IReadOnlyDictionary<string, long> epic)
    {
        var changed = 0;
        var now = _c.Now;
        foreach (var (app, seconds) in epic)
        {
            if (_c.Library.Summary(app) is null)
            {
                continue;
            }

            var before = _c.Games.Get(app);
            if (Reconcile(before, seconds, now) == before)
            {
                continue;
            }

            await _c.Games.UpdateAsync(app, e => Reconcile(e, seconds, now)).ConfigureAwait(false);
            changed++;
        }

        if (changed > 0)
        {
            _c.Log.Info("playtime", $"imported Epic's playtime for {changed} game{(changed == 1 ? "" : "s")}");
        }

        return changed;
    }

    /// <summary>
    /// Folds a fresh figure from Epic into a game's entry so no second is counted twice or lost, whether or not Epic records play
    /// started from Glider (that can differ per game: a game reporting through Epic's online services may be counted wherever it
    /// starts). Glider play since the last settlement is pending; when Epic's record grows by about that much, Epic recorded it and it
    /// is absorbed; when it hasn't grown <see cref="SettleAfter"/> after the last session, it is Glider's own. First import: Glider's
    /// history is treated as Glider's own (Epic did not record a Glider-launched session in the 2026-11-17 check).
    /// Known limit: playing the same game through Epic's launcher while Glider play is still pending can be read as Epic recording the
    /// Glider play (undercounting by the smaller of the two).
    /// </summary>
    internal static GameEntry Reconcile(GameEntry e, long epic, DateTimeOffset now)
    {
        if (e.EpicPlaytimeSeconds is not { } was)
        {
            return e with { EpicPlaytimeSeconds = epic, GliderOnlySeconds = e.PlaytimeSeconds, SettledLocalSeconds = e.PlaytimeSeconds };
        }

        var grew = Math.Max(0, epic - was);
        var pending = e.PendingLocalSeconds;
        if (pending == 0)
        {
            return e with { EpicPlaytimeSeconds = epic };
        }

        var absorbed = Math.Min(grew, pending);
        if (absorbed >= pending * AbsorbedShare)
        {
            return e with { EpicPlaytimeSeconds = epic, SettledLocalSeconds = e.PlaytimeSeconds }; // Epic recorded the Glider play
        }

        var settled = e with { EpicPlaytimeSeconds = epic, SettledLocalSeconds = e.SettledLocalSeconds + absorbed };
        return e.LastPlayedAt is { } last && now - last < SettleAfter
            ? settled // Epic may still post it: keep it pending (shown either way)
            : settled with { GliderOnlySeconds = e.GliderOnlySeconds + (pending - absorbed), SettledLocalSeconds = e.PlaytimeSeconds };
    }

    /// <summary>
    /// The service's answer: a JSON array of <c>{ accountId, artifactId, totalTime }</c>, totalTime in seconds, artifactId = the app
    /// name. Anything else is ignored; a malformed body is null. Duplicates keep the larger figure.
    /// </summary>
    internal static IReadOnlyDictionary<string, long>? Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { AllowTrailingCommas = true });
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var app = LegendaryFiles.Str(item, "artifactId");
                var seconds = LegendaryFiles.Long(item, "totalTime");
                if (string.IsNullOrWhiteSpace(app) || seconds is not { } s || s < 0)
                {
                    continue;
                }

                map[app] = map.TryGetValue(app, out var had) ? Math.Max(had, s) : s;
            }

            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
