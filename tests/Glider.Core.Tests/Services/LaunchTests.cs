using Glider.Core.Contracts;
using Glider.Core.Game;

namespace Glider.Core.Tests.Services;

public class LaunchTests
{
    private static async Task<(Rig Rig, List<RunState> States)> Ready(bool eacInstalled = true, bool withSetup = true)
    {
        var rig = new Rig();
        var rec = rig.Install("v2");
        if (eacInstalled)
        {
            rig.Services.Installed.Add("EasyAntiCheat_EOS");
        }

        if (withSetup)
        {
            var setup = Path.Combine(rec.InstallPath, EacService.SetupRelative.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
            File.WriteAllText(setup, "");
        }

        rig.Settings.Set(Settings.Default with { LaunchArgs = "-nosplash" });
        await rig.StartWarmAsync();
        var states = new List<RunState>();
        rig.Service.Hub.StateChanged += s =>
        {
            lock (states)
            {
                if (states.Count == 0 || states[^1] != s.RunState)
                {
                    states.Add(s.RunState);
                }
            }
        };
        return (rig, states);
    }

    [Fact]
    public async Task FullLifecycle_EacAlreadyInstalled()
    {
        var (rig, states) = await Ready();
        using var _ = rig;

        var result = await rig.Service.Launch();
        Assert.True(result.Started);
        Assert.False(result.EacInstalled);
        Assert.Empty(rig.Host.Elevated);
        Assert.Single(rig.Processes.Started);
        Assert.Equal("-nosplash", rig.Legendary.LastExtraArgs);
        Assert.True(rig.Legendary.LastSkipVersionCheck); // pre-warm just checked, no update

        var w = rig.Service.LaunchServiceForTests.Watcher;
        Assert.True(w.Tick()); // stub alive, Shipping not yet
        Assert.Equal(RunState.Launching, rig.Service.Hub.State.RunState);

        var since = rig.Time.GetUtcNow().AddSeconds(-3);
        rig.Processes.Add(FortniteLayout.ShippingProcessName, since);
        rig.Processes.Remove("FortniteLauncher"); // stub exits after spawning the game
        Assert.True(w.Tick());
        Assert.Equal(RunState.Running, rig.Service.Hub.State.RunState);
        Assert.Equal(since, rig.Service.Hub.State.RunningSince);
        Assert.Equal(1, rig.Host.HideCount);

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Launch());
        Assert.Equal(ErrorCode.GameRunning, e.Error.Code);

        rig.Processes.Remove(FortniteLayout.ShippingProcessName);
        Assert.True(w.Tick());
        Assert.False(w.Tick());
        Assert.Equal(RunState.Idle, rig.Service.Hub.State.RunState);
        Assert.Null(rig.Service.Hub.State.RunningSince);
        Assert.Equal(1, rig.Host.RestoreCount);

        Assert.Equal([RunState.Preparing, RunState.Launching, RunState.Running, RunState.Closing, RunState.Idle], states);
    }

    [Fact]
    public async Task RunsEacSetupElevated_WhenServiceMissing()
    {
        var (rig, states) = await Ready(eacInstalled: false);
        using var _ = rig;

        var result = await rig.Service.Launch();
        Assert.True(result.EacInstalled);
        var (exe, args) = Assert.Single(rig.Host.Elevated);
        Assert.EndsWith("EasyAntiCheat_EOS_Setup.exe", exe, StringComparison.Ordinal);
        Assert.Equal("install prod-fn", args);
        Assert.Contains(RunState.InstallingPrerequisites, states);

        // Installed now: the next launch doesn't prompt again.
        rig.Processes.Remove("FortniteLauncher");
        rig.Service.LaunchServiceForTests.Watcher.Tick();
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        rig.Service.LaunchServiceForTests.Watcher.Tick();
        Assert.False((await rig.Service.Launch()).EacInstalled);
        Assert.Single(rig.Host.Elevated);
    }

    [Fact]
    public async Task EacProductIdFromSettingsJson()
    {
        var (rig, _) = await Ready(eacInstalled: false);
        using var __ = rig;
        var dir = Path.Combine(rig.Records.Current!.InstallPath, "FortniteGame", "Binaries", "Win64", "EasyAntiCheat");
        File.WriteAllText(Path.Combine(dir, "Settings.json"), """{"productid":"prod-xyz","sandboxid":"s"}""");
        await rig.Service.Launch();
        Assert.Equal("install prod-xyz", rig.Host.Elevated.Single().Args);
    }

    [Fact]
    public async Task UacDeclined_FailsWithEacSetupFailed_AndReturnsToIdle()
    {
        var (rig, _) = await Ready(eacInstalled: false);
        using var __ = rig;
        rig.Host.ElevatedResult = null;

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Launch());
        Assert.Equal(ErrorCode.EacSetupFailed, e.Error.Code);
        Assert.Equal(RunState.Idle, rig.Service.Hub.State.RunState);
        Assert.Empty(rig.Processes.Started);
        Assert.False(rig.Calls.Has("legendary.launch"));
    }

    [Fact]
    public async Task EacSetupNonZeroExit_Fails()
    {
        var (rig, _) = await Ready(eacInstalled: false);
        using var __ = rig;
        rig.Host.ElevatedResult = 3;
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Launch());
        Assert.Equal(ErrorCode.EacSetupFailed, e.Error.Code);
    }

    [Fact]
    public async Task LaunchTimesOutWhenShippingNeverAppears()
    {
        var (rig, _) = await Ready();
        using var __ = rig;
        await rig.Service.Launch();
        var w = rig.Service.LaunchServiceForTests.Watcher;
        rig.Processes.Remove("FortniteLauncher");
        Assert.True(w.Tick()); // stub just exited: grace period
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        Assert.False(w.Tick());
        Assert.Equal(RunState.Idle, rig.Service.Hub.State.RunState);
        Assert.Contains(rig.Sink.Of<Notice>(RpcEvents.Notice), n => n.Level == NoticeLevel.Error);
    }

    [Fact]
    public async Task RefusesWhileJobActive_AndChecksVersionWhenUpdateUnknown()
    {
        var (rig, _) = await Ready();
        using var __ = rig;
        var job = rig.Jobs.Enqueue(new JobRequest(JobKind.Verify, null, null, null, null));
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Launch());
        Assert.Equal(ErrorCode.Busy, e.Error.Code);
        rig.Jobs.Dismiss(job.Id);

        rig.Time.Advance(TimeSpan.FromMinutes(20)); // last check is stale → let Legendary check
        await rig.Service.Launch();
        Assert.False(rig.Legendary.LastSkipVersionCheck);
    }

    [Fact]
    public async Task StartFailureIsLaunchFailed()
    {
        var (rig, _) = await Ready();
        using var __ = rig;
        rig.Processes.StartError = new System.ComponentModel.Win32Exception(2);
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Launch());
        Assert.Equal(ErrorCode.LaunchFailed, e.Error.Code);
        Assert.Equal(RunState.Idle, rig.Service.Hub.State.RunState);
        Assert.False(rig.Service.LaunchServiceForTests.Watcher.IsActive);
    }

    [Fact]
    public async Task DetectsAlreadyRunningGameAtStartup_WithoutHidingWindow()
    {
        using var rig = new Rig();
        rig.Install("v2");
        rig.Processes.Add(FortniteLayout.ShippingProcessName);
        await rig.Service.StartAsync();
        var first = rig.Sink.Of<AppSnapshot>(RpcEvents.State)[0];
        Assert.Equal(RunState.Running, first.Game.RunState);
        Assert.NotNull(first.Game.RunningSince);
        Assert.Equal(0, rig.Host.HideCount);
    }
}
