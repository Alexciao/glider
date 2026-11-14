using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

public class LaunchTests
{
    private const string Fn = FortniteLayout.AppName;
    private static readonly string EacSetupRelative = GameProfiles.Fortnite.Eac!.SetupRelative;

    private static Task<LaunchResult> Launch(Rig rig, string app = Fn, LaunchMode? mode = null) =>
        rig.Service.Launch(new LaunchRequest(app, mode, false));

    private static RunState Run(Rig rig, string app = Fn) => rig.Service.Hub.State.RunStateOf(app);

    private static bool Tick(Rig rig) => rig.Service.LaunchServiceForTests.Sessions.Tick();

    [Fact]
    public async Task CloseGameClosesShippingAndStubs_NotOtherProcesses()
    {
        using var rig = new Rig();
        rig.Install("v2");
        await rig.StartWarmAsync();
        rig.Processes.Add(FortniteLayout.ShippingProcessName);
        rig.Processes.Add("FortniteLauncher");
        rig.Processes.Add("FortniteBootstrapper");
        rig.Processes.Add("unrelated");

        await rig.Service.CloseGame(new AppRef(Fn));

        Assert.Equal(["FortniteBootstrapper", FortniteLayout.ShippingProcessName, "FortniteLauncher"], rig.Processes.Closed.Order());
        Assert.Equal(3, rig.Processes.Closed.Count);
        Assert.DoesNotContain("unrelated", rig.Processes.Closed);
        Assert.Empty(rig.Processes.Find(FortniteLayout.ShippingProcessName));
        await rig.Service.CloseGame(new AppRef(Fn)); // not running: a no-op
    }

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
            var setup = Path.Combine(rec.InstallPath, EacSetupRelative.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
            File.WriteAllText(setup, "");
        }

        rig.Games.Put(Fn, rig.Games.Get(Fn) with { Settings = GameSettings.Default with { LaunchArgs = "-nosplash" } });
        await rig.StartWarmAsync();
        var states = new List<RunState>();
        rig.Service.Hub.StateChanged += s =>
        {
            lock (states)
            {
                var run = s.RunStateOf(Fn);
                if (states.Count == 0 ? run != RunState.Idle : states[^1] != run)
                {
                    states.Add(run);
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

        var result = await Launch(rig);
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.False(result.Offline);
        Assert.Empty(rig.Host.Elevated);
        Assert.Single(rig.Processes.Started);
        Assert.Equal("-nosplash", rig.Legendary.LastExtraArgs); // per-game launch args
        Assert.True(rig.Legendary.LastSkipVersionCheck); // the launch lane always skips Legendary's version check

        Assert.True(Tick(rig)); // stub alive, Shipping not yet
        Assert.Equal(RunState.Launching, Run(rig));

        var since = rig.Time.GetUtcNow().AddSeconds(1);
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        rig.Processes.Spawn(FortniteLayout.ShippingProcessName, parentId: 999, start: since); // re-parented by the anti-cheat service
        rig.Processes.Remove("FortniteLauncher"); // stub exits after spawning the game
        Assert.True(Tick(rig));
        Assert.Equal(RunState.Running, Run(rig));
        Assert.Equal(since, rig.Service.Hub.State.Sessions[Fn].Since);
        Assert.Equal(1, rig.Host.HideCount);
        Assert.Single((await rig.Service.GetSnapshot()).Sessions);

        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig));
        Assert.Equal(ErrorCode.GameRunning, e.Error.Code);

        rig.Processes.Remove(FortniteLayout.ShippingProcessName);
        Assert.True(Tick(rig)); // Closing
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(Tick(rig)); // nothing back for 5 s: ended
        Assert.Equal(RunState.Idle, Run(rig));
        Assert.Empty(rig.Service.Hub.State.Sessions);
        Assert.Equal(1, rig.Host.RestoreCount);

        Assert.Equal([RunState.Preparing, RunState.Launching, RunState.Running, RunState.Closing, RunState.Idle], states);
    }

    /// <summary>The real v0.1 failure (audit §0.3): Fortnite 43.x starts FortniteBootstrapper, which v0.1 didn't know.</summary>
    [Fact]
    public async Task FortniteBootstrapperChain_IsTrackedThroughTheProcessTree_AndPlaytimeIsRecorded()
    {
        var (rig, _) = await Ready();
        using var __ = rig;
        ProcessEntry? root = null;
        rig.Processes.OnStart = (_, p) => root = p;
        await Launch(rig);

        // launcher (spawned) → bootstrapper (child) → EAC wrapper (grandchild); then the stubs exit.
        var boot = rig.Processes.Spawn("FortniteBootstrapper", root!.Id);
        Assert.True(Tick(rig));
        var wrapper = rig.Processes.Spawn("FortniteClient-Win64-Shipping_EAC_EOS", boot.Id);
        rig.Processes.Exit(root.Id);
        Assert.True(Tick(rig));
        rig.Processes.Exit(boot.Id);
        rig.Time.Advance(TimeSpan.FromSeconds(8));
        Assert.True(Tick(rig));
        Assert.Equal(RunState.Launching, Run(rig)); // the wrapper keeps the session alive

        var shipping = rig.Processes.Spawn(FortniteLayout.ShippingProcessName, parentId: 4); // started by the EAC service
        rig.Processes.Exit(wrapper.Id);
        Assert.True(Tick(rig));
        Assert.Equal(RunState.Running, Run(rig));

        rig.Time.Advance(TimeSpan.FromMinutes(10));
        rig.Processes.Exit(shipping.Id);
        Assert.True(Tick(rig));
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(Tick(rig));
        await rig.Service.LaunchServiceForTests.PlaytimeFlushed;
        Assert.InRange(rig.Games.Get(Fn).PlaytimeSeconds, 600, 620);
        Assert.NotNull(rig.Games.Get(Fn).LastPlayedAt);
        Assert.DoesNotContain(rig.Sink.Of<Notice>(RpcEvents.Notice), n => n.Level == NoticeLevel.Error);
    }

    [Fact]
    public async Task GenericGame_IsRunningWhileAnyProcessOfItsTreeRuns()
    {
        using var rig = new Rig();
        rig.AddGame("Indie", "Indie Game", installedVersion: "1.0");
        await rig.StartWarmAsync();
        ProcessEntry? root = null;
        rig.Processes.OnStart = (_, p) => root = p;

        await Launch(rig, "Indie");
        Assert.Equal("Indie", rig.Legendary.LastLaunchedApp);
        Assert.True(Tick(rig));
        Assert.Equal(RunState.Running, Run(rig, "Indie")); // no profile: the spawned exe itself is the game

        var child = rig.Processes.Spawn("IndieRenderer", root!.Id);
        Assert.True(Tick(rig));
        rig.Processes.Exit(root.Id); // the launcher exits, its child keeps running
        Assert.True(Tick(rig));
        Assert.Equal(RunState.Running, Run(rig, "Indie"));

        rig.Processes.Spawn("IndieRenderer", parentId: 4, start: rig.Time.GetUtcNow().AddHours(-1)); // an unrelated older one
        rig.Processes.Exit(child.Id);
        Assert.True(Tick(rig));
        Assert.Equal(RunState.Closing, Run(rig, "Indie"));
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(Tick(rig));
        Assert.Equal(RunState.Idle, Run(rig, "Indie"));
    }

    [Fact]
    public async Task TwoGamesRunAtOnce_EachWithItsOwnSession()
    {
        var (rig, _) = await Ready();
        using var __ = rig;
        rig.AddGame("Indie", "Indie Game", installedVersion: "1.0");
        await rig.SummaryAsync("Indie");

        await Launch(rig);
        await Launch(rig, "Indie");
        Tick(rig);
        Assert.Equal(RunState.Launching, Run(rig));
        Assert.Equal(RunState.Running, Run(rig, "Indie"));
        Assert.Equal(2, (await rig.Service.GetSnapshot()).Sessions.Count);
    }

    [Fact]
    public async Task RunsEacSetupElevated_WhenServiceMissing()
    {
        var (rig, states) = await Ready(eacInstalled: false);
        using var _ = rig;

        await Launch(rig);
        var (exe, args) = Assert.Single(rig.Host.Elevated);
        Assert.EndsWith("EasyAntiCheat_EOS_Setup.exe", exe, StringComparison.Ordinal);
        Assert.Equal("install prod-fn", args);
        Assert.Contains(RunState.InstallingPrerequisites, states);

        // Installed now: the next launch doesn't prompt again.
        StopGame(rig);
        await Launch(rig);
        Assert.Single(rig.Host.Elevated);
    }

    [Fact]
    public async Task EacProductIdFromSettingsJson()
    {
        var (rig, _) = await Ready(eacInstalled: false);
        using var __ = rig;
        var dir = Path.Combine(rig.Games.Record()!.InstallPath, "FortniteGame", "Binaries", "Win64", "EasyAntiCheat");
        File.WriteAllText(Path.Combine(dir, "Settings.json"), """{"productid":"prod-xyz","sandboxid":"s"}""");
        await Launch(rig);
        Assert.Equal("install prod-xyz", rig.Host.Elevated.Single().Args);
    }

    [Fact]
    public async Task GenericEacGame_RunsItsOwnSetupWithItsProductId()
    {
        using var rig = new Rig();
        rig.AddGame("Shooter", "Shooter", installedVersion: "1.0");
        var eac = Path.Combine(rig.Root, "Shooter", "EasyAntiCheat");
        Directory.CreateDirectory(eac);
        File.WriteAllText(Path.Combine(eac, "EasyAntiCheat_EOS_Setup.exe"), "");
        File.WriteAllText(Path.Combine(eac, "Settings.json"), """{"productid":"prod-shooter"}""");
        await rig.StartWarmAsync();

        await Launch(rig, "Shooter");
        var (exe, args) = Assert.Single(rig.Host.Elevated);
        Assert.StartsWith(eac, exe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("install prod-shooter", args);
        Assert.Equal(AntiCheat.EasyAntiCheat, (await rig.Service.GetGameDetail(new AppRef("Shooter"))).AntiCheat);
    }

    [Fact]
    public async Task UacDeclined_FailsWithEacSetupFailed_AndReturnsToIdle()
    {
        var (rig, _) = await Ready(eacInstalled: false);
        using var __ = rig;
        rig.Host.ElevatedResult = null;

        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig));
        Assert.Equal(ErrorCode.EacSetupFailed, e.Error.Code);
        Assert.Equal(RunState.Idle, Run(rig));
        Assert.Empty(rig.Processes.Started);
        Assert.False(rig.Calls.Has("legendary.launch:Fortnite"));
    }

    [Fact]
    public async Task EacSetupNonZeroExit_Fails()
    {
        var (rig, _) = await Ready(eacInstalled: false);
        using var __ = rig;
        rig.Host.ElevatedResult = 3;
        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig));
        Assert.Equal(ErrorCode.EacSetupFailed, e.Error.Code);
    }

    [Fact]
    public async Task LaunchFailsWhenEverythingExitsBeforeTheGameAppears()
    {
        var (rig, _) = await Ready();
        using var __ = rig;
        await Launch(rig);
        rig.Processes.Remove("FortniteLauncher");
        Assert.True(Tick(rig)); // stub just exited: grace period
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        Assert.False(Tick(rig));
        Assert.Equal(RunState.Idle, Run(rig));
        Assert.Contains(rig.Sink.Of<Notice>(RpcEvents.Notice), n => n.Level == NoticeLevel.Error && n.AppName == Fn);
    }

    [Fact]
    public async Task RefusesWhileThisGameHasAJob_ButNeverBecauseAnotherGameDownloads()
    {
        var (rig, _) = await Ready();
        using var __ = rig;
        rig.AddGame("Big", "Big Download");
        await rig.SummaryAsync("Big");

        var download = rig.Jobs.Enqueue(new JobRequest("Big", JobKind.Install, rig.Root, null, null, "1.0", "Big Download"));
        rig.Jobs.Set(rig.Jobs.Get(download.Id) with { Status = JobStatus.Running });
        Assert.Equal(LaunchOutcome.Started, (await Launch(rig)).Outcome); // the launch lane never waits behind a download
        StopGame(rig);

        var verify = rig.Jobs.Enqueue(new JobRequest(Fn, JobKind.Verify, null, null, null, null));
        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig));
        Assert.Equal(ErrorCode.GameBusy, e.Error.Code);
        Assert.Equal(Fn, e.Error.AppName);
        rig.Jobs.Dismiss(verify.Id);
    }

    [Fact]
    public async Task ALaunchableAddonIsRefusedWhileItsBaseGameUpdates()
    {
        var (rig, _) = await Ready();
        using var __ = rig;
        var addon = LibraryFixture.Item("Battle Royale", "fn", "addon", ["addons/launchable", "addons", "applications"]);
        addon["mainGameItem"] = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "4fe75bbc5a674f4f9b356b5c90567da5",
            ["releaseInfo"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["appId"] = Fn }),
        };
        rig.Library.Add("BR", addon).Save();
        rig.Legendary.Set("BR", FakeLegendary.MakeInstalled(rig.Games.Record()!.InstallPath, "1.0", "BR") with { IsDlc = true });
        Assert.Equal(GameKind.LaunchableAddon, (await rig.SummaryAsync("BR"))!.Kind);

        rig.Jobs.Enqueue(new JobRequest(Fn, JobKind.Update, null, null, "v1", "v2"));
        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig, "BR"));
        Assert.Equal(ErrorCode.GameBusy, e.Error.Code);
    }

    [Fact]
    public async Task StartFailureIsLaunchFailed()
    {
        var (rig, _) = await Ready();
        using var __ = rig;
        rig.Processes.StartError = new System.ComponentModel.Win32Exception(2);
        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig));
        Assert.Equal(ErrorCode.LaunchFailed, e.Error.Code);
        Assert.Equal(RunState.Idle, Run(rig));
        Assert.False(rig.Service.LaunchServiceForTests.Sessions.AnyActive);
    }

    [Fact]
    public async Task OfflineMode_PassesOfflineAndNeedsNoSignIn()
    {
        var (rig, _) = await Ready();
        using var __ = rig;
        await rig.Service.SignOut();
        var r = await Launch(rig, mode: LaunchMode.Offline);
        Assert.True(r.Offline);
        Assert.True(rig.Legendary.LastOffline);
        Assert.True(rig.Service.Hub.State.Sessions[Fn].Offline);
    }

    [Fact]
    public async Task DetectsAlreadyRunningGameAtStartup_WithoutHidingWindow()
    {
        using var rig = new Rig();
        rig.Install("v2");
        rig.Processes.Add(FortniteLayout.ShippingProcessName);
        await rig.StartWarmAsync();
        var session = Assert.Single((await rig.Service.GetSnapshot()).Sessions);
        Assert.Equal(RunState.Running, session.State);
        Assert.NotNull(session.Since);
        Assert.Equal(0, rig.Host.HideCount);
        Assert.Equal(RunState.Running, (await rig.SummaryAsync())!.RunState);
    }

    private static int LaunchCalls(Rig rig) => rig.Calls.Count("legendary.launch:Fortnite");

    [Fact]
    public async Task PreparedLaunchCommandIsUsedOnceAndSkipsLegendaryOnClick()
    {
        var (rig, _) = await Ready();
        using var _r = rig;

        await rig.Service.PrepareLaunch(new AppRef(Fn));
        await rig.Service.PrepareLaunch(new AppRef(Fn)); // repeated hover: no second fetch
        await rig.Service.LaunchServiceForTests.Preparing;
        Assert.Equal(1, LaunchCalls(rig));

        Assert.Equal(LaunchOutcome.Started, (await Launch(rig)).Outcome);
        Assert.Equal(1, LaunchCalls(rig)); // the click reused the prepared command
        Assert.Contains(rig.Log.Lines, l => l.Contains("prepared=True", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("secret", StringComparison.Ordinal)); // exchange code never logged
    }

    [Fact]
    public async Task ExpiredOrMismatchedPreparedCommandIsFetchedAgain()
    {
        var (rig, _) = await Ready();
        using var _r = rig;

        await rig.Service.PrepareLaunch(new AppRef(Fn));
        await rig.Service.LaunchServiceForTests.Preparing;
        rig.Time.Advance(LaunchService.PreparedTtl + TimeSpan.FromSeconds(1));
        await Launch(rig);
        Assert.Equal(2, LaunchCalls(rig));
        Assert.Contains(rig.Log.Lines, l => l.Contains("prepared=False", StringComparison.Ordinal));
        StopGame(rig);

        await rig.Service.PrepareLaunch(new AppRef(Fn));
        await rig.Service.LaunchServiceForTests.Preparing;
        await rig.Service.UpdateGameSettings(new GameSettingsRequest(Fn, GameSettings.Default with { LaunchArgs = "-other" })); // the prepared command baked in the old args
        await Launch(rig);
        Assert.Equal(4, LaunchCalls(rig));
        Assert.Equal("-other", rig.Legendary.LastExtraArgs);
    }

    [Fact]
    public async Task PrepareDoesNothingWhenALaunchCouldNotStart()
    {
        var (rig, _) = await Ready();
        using var _r = rig;

        rig.Jobs.Enqueue(new JobRequest(Fn, JobKind.Verify, null, null, null, null));
        await rig.Service.PrepareLaunch(new AppRef(Fn));
        await rig.Service.LaunchServiceForTests.Preparing;
        Assert.Equal(0, LaunchCalls(rig));
    }

    [Fact]
    public async Task PrepareIsANoOpWhenSignedOut()
    {
        var (rig, _) = await Ready();
        using var _r = rig;

        await rig.Service.SignOut();
        await rig.Service.PrepareLaunch(new AppRef(Fn));
        await rig.Service.LaunchServiceForTests.Preparing;
        Assert.Equal(0, LaunchCalls(rig));
    }

    private static void StopGame(Rig rig)
    {
        rig.Processes.Remove("FortniteLauncher");
        rig.Processes.Remove(FortniteLayout.ShippingProcessName);
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        while (Tick(rig))
        {
            rig.Time.Advance(TimeSpan.FromSeconds(11));
        }
    }
}

/// <summary>Security review: Glider must not borrow its UAC prompt for a swapped file in the user-writable game folder.</summary>
public class EacElevationSafetyTests
{
    private static async Task<Rig> ReadyWithoutEac()
    {
        var rig = new Rig();
        var rec = rig.Install("v2");
        var setup = Path.Combine(rec.InstallPath, GameProfiles.Fortnite.Eac!.SetupRelative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
        File.WriteAllText(setup, "");
        await rig.StartWarmAsync();
        return rig;
    }

    [Fact]
    public async Task AnUnsignedSetupIsNeverRunElevated()
    {
        using var rig = await ReadyWithoutEac();
        rig.Publisher.Reason = "it has no valid digital signature";

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Launch(new LaunchRequest(FortniteLayout.AppName, null, false)));
        Assert.Equal(ErrorCode.EacSetupFailed, e.Error.Code);
        Assert.Contains("signature", e.Error.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.Host.Elevated);
        Assert.Empty(rig.Processes.Started);
        Assert.EndsWith("EasyAntiCheat_EOS_Setup.exe", Assert.Single(rig.Publisher.Checked), StringComparison.Ordinal);
    }

    [Fact]
    public void AGameWithoutEasyAntiCheatHasNoSetup_AndOneWithoutAProductIdIsNotRun()
    {
        using var dir = new TestSupport.TempDir();
        Assert.Null(EacService.ResolveSetup("Indie", dir.Path));
        var eac = dir.Combine("EasyAntiCheat");
        Directory.CreateDirectory(eac);
        File.WriteAllText(Path.Combine(eac, "Settings.json"), """{"productid":"../../evil"}""");
        File.WriteAllText(Path.Combine(eac, "EasyAntiCheat_EOS_Setup.exe"), "");
        Assert.Null(EacService.ResolveSetup("Indie", dir.Path)); // an unsafe product id is never passed to an elevated process
    }

    [Fact]
    public void TheRealCheckRejectsAnUnsignedFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"glider-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0, 0]);
        try
        {
            Assert.NotNull(new AuthenticodePublisherCheck().WhyUntrusted(path));
            // CoreLib carries an embedded Microsoft Authenticode signature: valid, but not Epic's.
            var microsoftSigned = typeof(object).Assembly.Location;
            Assert.Contains("not by Epic", new AuthenticodePublisherCheck().WhyUntrusted(microsoftSigned) ?? "", StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
