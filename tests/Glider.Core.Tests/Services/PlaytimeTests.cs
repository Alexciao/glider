using Glider.Core.Contracts;
using Glider.Core.Game;

namespace Glider.Core.Tests.Services;

public class PlaytimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AccumulatesWholeSecondsAndCarriesFractions()
    {
        var t = new PlaytimeTracker();
        t.Start(T0);
        Assert.Equal(1, t.Take(T0.AddSeconds(1.6)));      // 0.6 s carried
        Assert.Equal(1, t.Take(T0.AddSeconds(2.7)));      // 1.1 s since checkpoint -> 1, 0.1 carried
        Assert.Equal(8, t.Take(T0.AddSeconds(10), final: true));
        Assert.False(t.Active);
        Assert.Equal(0, t.Take(T0.AddSeconds(99)));
    }

    [Fact]
    public void ClockStepBackNeverSubtracts()
    {
        var t = new PlaytimeTracker();
        t.Start(T0);
        Assert.Equal(0, t.Take(T0.AddSeconds(-30)));
        Assert.Equal(5, t.Take(T0.AddSeconds(5)));
    }

    [Fact]
    public void ApplyAddsToTotalAndStampsLastPlayed()
    {
        var entry = GameEntry.Empty with { PlaytimeSeconds = 100 };
        var next = PlaytimeTracker.Apply(entry, 42, T0.AddHours(1));
        Assert.Equal(142, next.PlaytimeSeconds);
        Assert.Equal(T0.AddHours(1), next.LastPlayedAt);
    }

    [Fact]
    public void ApplyKeepsTheSessionStartAcrossCheckpoints()
    {
        var t = new PlaytimeTracker();
        t.Start(T0);
        var first = PlaytimeTracker.Apply(GameEntry.Empty, 0, T0, t.Started);
        t.Take(T0.AddMinutes(5), final: true);
        var last = PlaytimeTracker.Apply(first, 300, T0.AddMinutes(5), t.Started);
        Assert.Equal(T0, last.LastStartedAt);
        Assert.Equal(T0.AddMinutes(5), last.LastPlayedAt);
        Assert.Equal(T0, PlaytimeTracker.Apply(last, 0, T0.AddHours(1)).LastStartedAt); // no tracker: unchanged
    }

    [Fact]
    public void BreakdownAddsUpToTheShownTotal()
    {
        var imported = GameEntry.Empty with { PlaytimeSeconds = 5000, EpicPlaytimeSeconds = 40_000, GliderOnlySeconds = 600, SettledLocalSeconds = 4000, LastStartedAt = T0 };
        var b = imported.Breakdown;
        Assert.Equal(40_000, b.EpicSeconds);
        Assert.Equal(1600, b.GliderSeconds); // 600 Glider-only + 1000 pending
        Assert.Equal(imported.TotalPlaytimeSeconds, b.EpicSeconds!.Value + b.GliderSeconds);
        Assert.Equal(T0, b.LastStartedAt);

        var never = (GameEntry.Empty with { PlaytimeSeconds = 777 }).Breakdown;
        Assert.Null(never.EpicSeconds);
        Assert.Equal(777, never.GliderSeconds);
        Assert.Null(never.LastStartedAt);
    }

    [Fact]
    public async Task SessionIsPersistedPerGameWhenItExits()
    {
        // The test drives sessions.Tick() itself; the background watch loop would tick at intermediate fake times while
        // Advance() steps through its timers (ending the session at t+130) and race the manual ticks.
        using var rig = new Rig { ProcessPollInterval = TimeSpan.FromDays(1) };
        var rec = rig.Install("v2");
        var setup = Path.Combine(rec.InstallPath, GameProfiles.Fortnite.Eac!.SetupRelative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
        File.WriteAllText(setup, "");
        rig.Services.Installed.Add("EasyAntiCheat_EOS");
        await rig.StartWarmAsync();

        await rig.Service.Launch(new LaunchRequest(FortniteLayout.AppName, null, false));
        var sessions = rig.Service.LaunchServiceForTests.Sessions;
        var since = rig.Time.GetUtcNow();
        rig.Processes.Add(FortniteLayout.ShippingProcessName, since);
        rig.Processes.Remove("FortniteLauncher");
        Assert.True(sessions.Tick());
        Assert.Equal(RunState.Running, rig.Service.Hub.State.RunStateOf(FortniteLayout.AppName));

        rig.Time.Advance(TimeSpan.FromSeconds(125));
        rig.Processes.Remove(FortniteLayout.ShippingProcessName);
        sessions.Tick();
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        sessions.Tick();
        await rig.Service.LaunchServiceForTests.PlaytimeFlushed;

        var entry = rig.Games.Get(FortniteLayout.AppName);
        Assert.Equal(131, entry.PlaytimeSeconds); // until the session ended (5 s grace after the last process)
        Assert.Equal(rig.Time.GetUtcNow(), entry.LastPlayedAt);
        var game = (await rig.SummaryAsync())!;
        Assert.Equal(131, game.PlaytimeSeconds);
        Assert.Equal(rig.Time.GetUtcNow(), game.LastPlayedAt);
    }

    [Fact]
    public async Task QuittingWhileAGameRunsKeepsThePlaytimeSinceTheLastFlush() // review L7
    {
        using var rig = new Rig { ProcessPollInterval = TimeSpan.FromDays(1) };
        var rec = rig.Install("v2");
        var setup = Path.Combine(rec.InstallPath, GameProfiles.Fortnite.Eac!.SetupRelative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
        File.WriteAllText(setup, "");
        rig.Services.Installed.Add("EasyAntiCheat_EOS");
        await rig.StartWarmAsync();

        await rig.Service.Launch(new LaunchRequest(FortniteLayout.AppName, null, false));
        var launch = rig.Service.LaunchServiceForTests;
        rig.Processes.Add(FortniteLayout.ShippingProcessName, rig.Time.GetUtcNow());
        Assert.True(launch.Sessions.Tick());
        await launch.PlaytimeFlushed;

        rig.Time.Advance(TimeSpan.FromSeconds(20)); // less than the 30 s flush interval: nothing written yet
        Assert.Equal(0, rig.Games.Get(FortniteLayout.AppName).PlaytimeSeconds);

        launch.FlushPlaytimeForShutdown(TimeSpan.FromSeconds(5)); // what GliderService.Dispose does

        Assert.Equal(20, rig.Games.Get(FortniteLayout.AppName).PlaytimeSeconds);
        launch.FlushPlaytimeForShutdown(TimeSpan.FromSeconds(5)); // idempotent: nothing is counted twice
        Assert.Equal(20, rig.Games.Get(FortniteLayout.AppName).PlaytimeSeconds);
    }
}
