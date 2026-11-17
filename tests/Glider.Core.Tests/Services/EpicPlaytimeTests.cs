using System.Net;
using System.Text;
using Glider.Core.Game;
using Glider.Core.Legendary;

namespace Glider.Core.Tests.Services;

public class EpicPlaytimeTests
{
    private const string Token = "eg1~SECRET-ACCESS-TOKEN";

    /// <summary>Answers every request with one scripted response and remembers what was asked.</summary>
    private sealed class Script(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static void WriteSession(Rig rig, DateTimeOffset expires, string account = "acc123") =>
        File.WriteAllText(LegendaryFiles.UserJson(rig.Paths.LegendaryConfig),
            $$"""{"access_token":"{{Token}}","account_id":"{{account}}","displayName":"Player","token_type":"bearer","expires_at":"{{expires.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}"}""");

    [Fact]
    public void ParseReadsArtifactIdAndSecondsAndSkipsJunk()
    {
        var map = EpicPlaytimeService.Parse(Encoding.UTF8.GetBytes(
            """[{"accountId":"a","artifactId":"Fortnite","totalTime":1557905},{"artifactId":"Sugar","totalTime":29275},{"artifactId":"Sugar","totalTime":10},{"artifactId":"","totalTime":5},{"artifactId":"Neg","totalTime":-1},{"artifactId":"Str","totalTime":"9"},{"totalTime":3},]"""));
        Assert.NotNull(map);
        Assert.Equal(2, map.Count);
        Assert.Equal(1557905, map["Fortnite"]);
        Assert.Equal(29275, map["Sugar"]); // duplicates keep the larger figure
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("")]
    public void ParseRefusesAnythingButAnArray(string body) => Assert.Null(EpicPlaytimeService.Parse(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public async Task SessionReaderReturnsTheTokenButNeverPrintsIt()
    {
        using var rig = new Rig();
        WriteSession(rig, rig.Time.GetUtcNow().AddHours(4));
        var s = await LegendaryFiles.ReadSessionAsync(LegendaryFiles.UserJson(rig.Paths.LegendaryConfig), CancellationToken.None);
        Assert.NotNull(s);
        Assert.Equal("acc123", s.AccountId);
        Assert.Equal(Token, s.AccessToken);
        Assert.DoesNotContain(Token, s.ToString(), StringComparison.Ordinal);

        File.WriteAllText(LegendaryFiles.UserJson(rig.Paths.LegendaryConfig), """{"account_id":"acc123","expires_at":"2026-01-01T00:00:00Z"}""");
        Assert.Null(await LegendaryFiles.ReadSessionAsync(LegendaryFiles.UserJson(rig.Paths.LegendaryConfig), CancellationToken.None));
    }

    [Fact]
    public async Task ImportStoresEpicsFigureForLibraryGamesAndTheSummaryAddsGlidersOwn()
    {
        var handler = new Script(() => Json("""[{"accountId":"acc123","artifactId":"Fortnite","totalTime":7200},{"accountId":"acc123","artifactId":"UE_5.0","totalTime":900}]"""));
        using var rig = new Rig { EpicPlaytimeHandler = handler };
        await rig.Games.UpdateAsync(FortniteLayout.AppName, e => e with { PlaytimeSeconds = 600 });
        WriteSession(rig, rig.Time.GetUtcNow().AddHours(4));
        await rig.StartWarmAsync();

        Assert.Equal(1, await rig.Service.EpicPlaytimeForTests.RefreshAsync(CancellationToken.None));

        var request = Assert.Single(handler.Seen);
        Assert.Equal("https://library-service.live.use1a.on.epicgames.com/library/api/public/playtime/account/acc123/all", request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Token, request.Headers.Authorization?.Parameter);
        Assert.Equal(7200, rig.Games.Get(FortniteLayout.AppName).EpicPlaytimeSeconds);
        Assert.False(rig.Games.Current.ContainsKey("UE_5.0")); // engines and tools aren't games in this library
        Assert.Equal(7800, (await rig.SummaryAsync())!.PlaytimeSeconds);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains(Token, StringComparison.Ordinal));

        Assert.Equal(0, await rig.Service.EpicPlaytimeForTests.RefreshAsync(CancellationToken.None)); // unchanged: nothing rewritten
    }

    [Fact]
    public async Task ExpiredOrMissingSessionMakesNoRequest()
    {
        var handler = new Script(() => Json("[]"));
        using var rig = new Rig { EpicPlaytimeHandler = handler };
        await rig.StartWarmAsync();
        Assert.Null(await rig.Service.EpicPlaytimeForTests.RefreshAsync(CancellationToken.None)); // no user.json

        WriteSession(rig, rig.Time.GetUtcNow().AddSeconds(30)); // about to expire: Legendary refreshes it first
        Assert.Null(await rig.Service.EpicPlaytimeForTests.RefreshAsync(CancellationToken.None));
        Assert.Empty(handler.Seen);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ARefusedOrFailedCallChangesNothing(HttpStatusCode code)
    {
        using var rig = new Rig { EpicPlaytimeHandler = new Script(() => Json("""{"errorCode":"x"}""", code)) };
        await rig.Games.UpdateAsync(FortniteLayout.AppName, e => e with { PlaytimeSeconds = 600, EpicPlaytimeSeconds = 100 });
        WriteSession(rig, rig.Time.GetUtcNow().AddHours(4));
        await rig.StartWarmAsync();

        Assert.Null(await rig.Service.EpicPlaytimeForTests.RefreshAsync(CancellationToken.None));
        Assert.Equal(100, rig.Games.Get(FortniteLayout.AppName).EpicPlaytimeSeconds);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>600 s played under Glider before the first import, Epic's record at 7200 s.</summary>
    private static GameEntry Imported() =>
        EpicPlaytimeService.Reconcile(GameEntry.Empty with { PlaytimeSeconds = 600, LastPlayedAt = T0.AddDays(-3) }, 7200, T0);

    /// <summary>A one-hour Glider session ending at <paramref name="end"/>.</summary>
    private static GameEntry PlayHour(GameEntry e, DateTimeOffset end) => e with { PlaytimeSeconds = e.PlaytimeSeconds + 3600, LastPlayedAt = end };

    [Fact]
    public void NeverImportedShowsGlidersOwnTime() =>
        Assert.Equal(600, (GameEntry.Empty with { PlaytimeSeconds = 600 }).TotalPlaytimeSeconds);

    [Fact]
    public void FirstImportAddsGlidersHistoryToEpicsRecord()
    {
        var e = Imported();
        Assert.Equal(7800, e.TotalPlaytimeSeconds);
        Assert.Equal(600, e.GliderOnlySeconds);
        Assert.Equal(0, e.PendingLocalSeconds);
    }

    [Fact]
    public void EpicNeverRecordsGliderPlay_TheHourIsKeptOnce()
    {
        var e = PlayHour(Imported(), T0.AddHours(1));
        Assert.Equal(11400, e.TotalPlaytimeSeconds);
        e = EpicPlaytimeService.Reconcile(e, 7200, T0.AddHours(1).AddMinutes(2)); // just after: still pending
        Assert.Equal(3600, e.PendingLocalSeconds);
        Assert.Equal(11400, e.TotalPlaytimeSeconds);
        e = EpicPlaytimeService.Reconcile(e, 7200, T0.AddHours(8)); // past SettleAfter: Glider's own
        Assert.Equal(0, e.PendingLocalSeconds);
        Assert.Equal(4200, e.GliderOnlySeconds);
        Assert.Equal(11400, e.TotalPlaytimeSeconds);
        Assert.Equal(11400, EpicPlaytimeService.Reconcile(e, 7200, T0.AddDays(2)).TotalPlaytimeSeconds); // stable
    }

    [Fact]
    public void EpicRecordsGliderPlayLate_TheHourIsNotCountedTwice()
    {
        var e = PlayHour(Imported(), T0.AddHours(1));
        e = EpicPlaytimeService.Reconcile(e, 7200, T0.AddHours(1).AddMinutes(2)); // not posted yet
        Assert.Equal(11400, e.TotalPlaytimeSeconds);
        e = EpicPlaytimeService.Reconcile(e, 7200 + 3590, T0.AddHours(2)); // posted (rounded): absorbed
        Assert.Equal(0, e.PendingLocalSeconds);
        Assert.Equal(600, e.GliderOnlySeconds);
        Assert.Equal(7200 + 3590 + 600, e.TotalPlaytimeSeconds);
        Assert.Equal(e, EpicPlaytimeService.Reconcile(e, 7200 + 3590, T0.AddDays(1))); // nothing left to settle
    }

    [Fact]
    public void PlayThroughEpicsLauncherOnlyAddsEpicsGrowth()
    {
        var e = EpicPlaytimeService.Reconcile(Imported(), 7200 + 1800, T0.AddHours(3));
        Assert.Equal(7800 + 1800, e.TotalPlaytimeSeconds);
    }

    [Fact]
    public void AFigureThatShrinksNeverSubtractsGliderPlay()
    {
        var e = PlayHour(Imported(), T0.AddHours(1));
        e = EpicPlaytimeService.Reconcile(e, 7000, T0.AddHours(8));
        Assert.Equal(7000 + 600 + 3600, e.TotalPlaytimeSeconds);
    }

    [Fact]
    public void ImportedEntriesArePersisted() => Assert.False((GameEntry.Empty with { EpicPlaytimeSeconds = 5 }).IsEmpty);
}
