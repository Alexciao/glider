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
        var rec = new GameRecord(@"D:\G", "v1", 1, T0, false, null, false, 100, null);
        var next = PlaytimeTracker.Apply(rec, 42, T0.AddHours(1));
        Assert.Equal(142, next.PlaytimeSeconds);
        Assert.Equal(T0.AddHours(1), next.LastPlayedAt);
    }

    [Fact]
    public async Task SessionIsPersistedWhenGameExits()
    {
        using var rig = new Rig();
        var rec = rig.Install("v2");
        var setup = Path.Combine(rec.InstallPath, EacService.SetupRelative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
        File.WriteAllText(setup, "");
        rig.Services.Installed.Add("EasyAntiCheat_EOS");
        await rig.StartWarmAsync();

        await rig.Service.Launch();
        var w = rig.Service.LaunchServiceForTests.Watcher;
        var since = rig.Time.GetUtcNow();
        rig.Processes.Add(FortniteLayout.ShippingProcessName, since);
        rig.Processes.Remove("FortniteLauncher");
        Assert.True(w.Tick());
        Assert.Equal(RunState.Running, rig.Service.Hub.State.RunState);

        rig.Time.Advance(TimeSpan.FromSeconds(125));
        rig.Processes.Remove(FortniteLayout.ShippingProcessName);
        w.Tick();
        w.Tick();
        await rig.Service.LaunchServiceForTests.PlaytimeFlushed;

        Assert.Equal(125, rig.Records.Current!.PlaytimeSeconds);
        Assert.Equal(rig.Time.GetUtcNow(), rig.Records.Current.LastPlayedAt);
        var game = rig.Service.Hub.BuildGame();
        Assert.Equal(125, game.PlaytimeSeconds);
        Assert.Equal(rig.Time.GetUtcNow(), game.LastPlayedAt);
    }
}
