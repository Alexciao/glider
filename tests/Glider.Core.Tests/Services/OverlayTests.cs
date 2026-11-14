using System.Text.Json.Nodes;
using Glider.Core.Contracts;

namespace Glider.Core.Tests.Services;

public class OverlayTests
{
    private const string App = JobInfo.OverlayAppName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Folder(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "EOSOVH-Win64-Shipping.dll"), "");
        return dir;
    }

    /// <summary>What `eos-overlay install` leaves behind: the folder, overlay_install.json and (optionally) the cached latest build.</summary>
    private static string ManagedInstall(Rig rig, string version = "1.0.0", string? latest = null, DateTimeOffset? checkedAt = null)
    {
        var dir = Folder(rig.Root, "managed-overlay");
        File.WriteAllText(Path.Combine(rig.Paths.LegendaryConfig, "overlay_install.json"), new JsonObject { ["version"] = version, ["install_path"] = dir }.ToJsonString());
        if (latest is not null)
        {
            var at = (checkedAt ?? rig.Time.GetUtcNow()).ToUnixTimeMilliseconds() / 1000.0;
            File.WriteAllText(Path.Combine(rig.Paths.LegendaryConfig, "overlay_version.json"),
                new JsonObject { ["last_update"] = at, ["data"] = new JsonObject { ["buildVersion"] = latest } }.ToJsonString());
        }

        return dir;
    }

    private static string EpicsOwnOverlay(Rig rig) =>
        Folder(rig.ProgramFilesX86, Path.Combine("Epic Games", "Epic Online Services", "managedArtifacts", Glider.Core.Overlay.OverlayEnvironment.OverlayAppId));

    private static async Task<Rig> StartedAsync(Action<Rig>? arrange = null)
    {
        var rig = new Rig();
        arrange?.Invoke(rig);
        await rig.StartWarmAsync();
        return rig;
    }

    [Fact]
    public async Task NothingInstalledIsTheCalmDefault()
    {
        using var rig = await StartedAsync();

        var o = (await rig.Service.GetSnapshot()).Overlay;

        Assert.Equal(new OverlayState(false, false, null, null, false, null, false, false, null, null), o);
    }

    [Fact]
    public async Task ManagedStateComesFromFilesAndTheRegistry_WithoutSpawningLegendary()
    {
        string folder = "";
        using var rig = await StartedAsync(r =>
        {
            folder = ManagedInstall(r, "1.0.0", latest: "1.1.0");
            r.OverlayRegistry.Enable(folder);
        });

        var o = (await rig.Service.GetSnapshot()).Overlay;

        Assert.True(o.Installed && o.Managed && o.Enabled);
        Assert.Equal(("1.0.0", "1.1.0", true), (o.Version, o.LatestVersion, o.UpdateAvailable));
        Assert.Equal(Path.GetFullPath(folder), o.InstallPath);
        Assert.Null(o.Note);
        Assert.DoesNotContain(rig.Calls.All, c => c.Contains("overlay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnInstallWhoseFolderIsGoneIsNotInstalled()
    {
        using var rig = await StartedAsync(r =>
        {
            var dir = ManagedInstall(r);
            Directory.Delete(dir, true);
        });

        Assert.False((await rig.Service.GetSnapshot()).Overlay.Installed);
    }

    [Fact]
    public async Task EpicsOwnOverlayCanBeSwitchedOnWithoutDownloadingAnything()
    {
        string theirs = "";
        using var rig = await StartedAsync(r => theirs = EpicsOwnOverlay(r));
        var before = (await rig.Service.GetSnapshot()).Overlay;
        Assert.False(before.Installed);
        Assert.Equal(Path.GetFullPath(theirs), before.ExternalPath);

        var after = await rig.Service.SetOverlayEnabled(new GameFlagRequest(App, true));

        Assert.True(after.Enabled);
        Assert.False(after.Managed); // Glider has no copy: update and remove don't apply
        Assert.Equal(Path.GetFullPath(theirs), after.InstallPath);
        Assert.Equal(Path.GetFullPath(theirs), Path.GetFullPath(rig.OverlayRegistry.Path!));
        Assert.Equal(2, rig.OverlayRegistry.Layers.Count);
        Assert.Empty(rig.Jobs.Requests);
    }

    [Fact]
    public async Task SwitchingOnAndOffTouchesOnlyTheUserRegistry()
    {
        string folder = "";
        using var rig = await StartedAsync(r => folder = ManagedInstall(r));

        var on = await rig.Service.SetOverlayEnabled(new GameFlagRequest(App, true));
        Assert.True(on.Enabled);
        Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(rig.OverlayRegistry.Path!));

        var off = await rig.Service.SetOverlayEnabled(new GameFlagRequest(App, false));
        Assert.False(off.Enabled);
        Assert.True(off.Installed); // still there, just off
        Assert.Null(rig.OverlayRegistry.Path);
        Assert.Empty(rig.OverlayRegistry.Layers);
        Assert.DoesNotContain(rig.Calls.All, c => c.Contains("overlay", StringComparison.OrdinalIgnoreCase)); // never `legendary eos-overlay disable` (HKLM)
    }

    [Fact]
    public async Task EnablingRepointsEntriesThatPointElsewhere()
    {
        using var rig = await StartedAsync(r =>
        {
            ManagedInstall(r);
            r.OverlayRegistry.Enable(Folder(r.Root, "old-overlay"));
        });

        await rig.Service.SetOverlayEnabled(new GameFlagRequest(App, true));

        Assert.Equal(["enable:" + Path.Combine(rig.Root, "old-overlay"), "disable"], rig.OverlayRegistry.Calls.Take(2));
        Assert.EndsWith("managed-overlay", rig.OverlayRegistry.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingOnNeedsSomethingInstalled_AndTheRightAppName()
    {
        using var rig = await StartedAsync();

        var none = await Assert.ThrowsAsync<GliderException>(() => rig.Service.SetOverlayEnabled(new GameFlagRequest(App, true)));
        Assert.Equal(ErrorCode.OverlayFailed, none.Error.Code);
        Assert.Contains("Install", none.Error.Message, StringComparison.Ordinal);
        var wrong = await Assert.ThrowsAsync<GliderException>(() => rig.Service.SetOverlayEnabled(new GameFlagRequest("Fortnite", true)));
        Assert.Equal(ErrorCode.InvalidRequest, wrong.Error.Code);
        Assert.Empty(rig.OverlayRegistry.Calls);
    }

    [Fact]
    public async Task OffExplainsWhatEpicsServicesLeaveInPlace()
    {
        using var rig = await StartedAsync(r =>
        {
            ManagedInstall(r);
            r.OverlayRegistry.MachineWide.Add(@"C:\Program Files (x86)\Epic Games\Epic Online Services\managedArtifacts\x\EOSOverlayVkLayer-Win64.json");
        });

        var off = (await rig.Service.GetSnapshot()).Overlay;
        Assert.Contains("all users", off.Note, StringComparison.Ordinal);

        await rig.Service.SetOverlayEnabled(new GameFlagRequest(App, true));
        Assert.Null((await rig.Service.GetSnapshot()).Overlay.Note);
    }

    [Fact]
    public async Task InstallQueuesAnOverlayJob_FirstIntoGlidersFolder_ThenAsAnUpdate()
    {
        using var rig = await StartedAsync();

        var first = await rig.Service.InstallOverlay();
        Assert.Equal((JobKind.Overlay, App), (first.Kind, first.AppName));
        Assert.Equal(rig.Paths.Overlay, first.TargetPath);
        Assert.True((await rig.Service.GetSnapshot()).Overlay.Busy);

        ManagedInstall(rig, "1.0.0", latest: "1.1.0");
        var second = await rig.Service.InstallOverlay();
        Assert.Null(second.TargetPath); // Legendary updates where it already installed
        Assert.Equal(("1.0.0", "1.1.0"), (second.FromVersion, second.ToVersion));
    }

    /// <summary>The queue stand-in doesn't run Legendary: do what the writer lane would (install, Legendary enables it), then finish the job.</summary>
    private static async Task<JobInfo> RunOverlayJobAsync(Rig rig, JobInfo job, bool legendaryEnables = true)
    {
        var outcome = await rig.Legendary.InstallOverlayAsync(job.TargetPath, new Progress<LegendaryProgress>(), Ct);
        Assert.True(outcome.Success);
        if (legendaryEnables)
        {
            var installed = JsonNode.Parse(File.ReadAllText(Path.Combine(rig.Paths.LegendaryConfig, "overlay_install.json")))!;
            rig.OverlayRegistry.Enable(installed["install_path"]!.GetValue<string>());
        }

        return await rig.Jobs.RunAsync(job.Id, rig.Service.Hooks, Ct);
    }

    [Fact]
    public async Task AFinishedInstallShowsInstalledEnabledAndSaysSo()
    {
        using var rig = await StartedAsync();
        rig.Legendary.OverlayVersion = "2.0.0";
        var job = await rig.Service.InstallOverlay();

        await RunOverlayJobAsync(rig, job);

        var o = (await rig.Service.GetSnapshot()).Overlay;
        Assert.True(o.Installed && o.Managed && o.Enabled);
        Assert.Equal("2.0.0", o.Version);
        Assert.False(o.Busy);
        Assert.Equal(rig.Paths.Overlay, rig.Legendary.LastOverlayPath);
        var notice = Assert.Single(rig.Sink.Of<Notice>("notice"), n => n.JobId == job.Id);
        Assert.Equal(("Epic overlay installed", NoticeLevel.Success), (notice.Title, notice.Level));
    }

    [Fact]
    public async Task AnUpdateKeepsTheOverlayOffWhenTheUserSwitchedItOff()
    {
        using var rig = await StartedAsync(r => ManagedInstall(r, "1.0.0", latest: "2.0.0")); // installed, switched off
        rig.Legendary.OverlayVersion = "2.0.0";
        var job = await rig.Service.InstallOverlay();

        await RunOverlayJobAsync(rig, job); // Legendary's update writes the registry again

        var o = (await rig.Service.GetSnapshot()).Overlay;
        Assert.Equal(("2.0.0", false, false), (o.Version, o.Enabled, o.UpdateAvailable));
        Assert.Null(rig.OverlayRegistry.Path);
        var notice = Assert.Single(rig.Sink.Of<Notice>("notice"), n => n.JobId == job.Id);
        Assert.Equal("Epic overlay is up to date", notice.Title);
    }

    [Fact]
    public async Task AnUpdateOfAnEnabledOverlayLeavesItOn()
    {
        string folder = "";
        using var rig = await StartedAsync(r =>
        {
            folder = ManagedInstall(r, "1.0.0", latest: "2.0.0");
            r.OverlayRegistry.Enable(folder);
        });
        rig.Legendary.OverlayVersion = "2.0.0";
        var job = await rig.Service.InstallOverlay();

        await RunOverlayJobAsync(rig, job, legendaryEnables: false);

        Assert.True((await rig.Service.GetSnapshot()).Overlay.Enabled);
        Assert.DoesNotContain("disable", rig.OverlayRegistry.Calls);
    }

    [Fact]
    public async Task AFailedJobTellsTheUser_ACancelledOneStaysQuiet()
    {
        using var rig = await StartedAsync();
        var job = await rig.Service.InstallOverlay();
        rig.Jobs.Set(rig.Jobs.Get(job.Id) with { Status = JobStatus.Failed, Error = Errors.OverlayFailed("boom") });
        await rig.Service.Hooks.OnFailedAsync(rig.Jobs.Get(job.Id), Ct);
        var notice = Assert.Single(rig.Sink.Of<Notice>("notice"));
        Assert.Equal((NoticeLevel.Error, "Epic overlay didn't install", FixAction.Retry), (notice.Level, notice.Title, notice.Action));

        var other = await rig.Service.InstallOverlay();
        rig.Jobs.Set(rig.Jobs.Get(other.Id) with { Status = JobStatus.Cancelled, Error = Errors.Cancelled() });
        await rig.Service.Hooks.OnFailedAsync(rig.Jobs.Get(other.Id), Ct);
        Assert.Single(rig.Sink.Of<Notice>("notice"));
    }

    [Fact]
    public async Task TheOverlayJobWaitsForRunningGamesToClose()
    {
        using var rig = await StartedAsync();
        var job = await rig.Service.InstallOverlay();
        rig.Service.Hub.Mutate(s => s with { Sessions = s.Sessions.SetItem("G", new RunningGame("G", "G", RunState.Running, null, false, false)) });

        var run = rig.Jobs.RunAsync(job.Id, rig.Service.Hooks, Ct);
        await Task.Delay(100, Ct);
        Assert.False(run.IsCompleted);
        Assert.Equal("Waiting for games to close", rig.Jobs.Get(job.Id).BlockedReason);

        rig.Service.Hub.Mutate(s => s with { Sessions = s.Sessions.Remove("G") });
        for (var i = 0; i < 100 && !run.IsCompleted; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(10, Ct);
        }

        Assert.True(run.IsCompleted);
    }

    [Fact]
    public async Task RemoveDeletesGlidersCopyAndClearsTheUserEntries()
    {
        string folder = "";
        using var rig = await StartedAsync(r =>
        {
            folder = ManagedInstall(r);
            r.OverlayRegistry.Enable(folder);
        });

        var o = await rig.Service.RemoveOverlay();

        Assert.False(o.Installed);
        Assert.False(o.Enabled);
        Assert.False(Directory.Exists(folder));
        Assert.Null(rig.OverlayRegistry.Path);
        Assert.Contains("legendary.overlayRemove", rig.Calls.All);
    }

    [Fact]
    public async Task RemovingGlidersCopyKeepsEpicsOwnOverlayOnIfThatWasTheOneInUse()
    {
        string theirs = "";
        using var rig = await StartedAsync(r =>
        {
            ManagedInstall(r);
            theirs = EpicsOwnOverlay(r);
            r.OverlayRegistry.Enable(theirs);
        });

        var o = await rig.Service.RemoveOverlay();

        Assert.False(o.Installed);
        Assert.True(o.Enabled);
        Assert.Equal(Path.GetFullPath(theirs), o.InstallPath);
    }

    [Fact]
    public async Task RemoveIsANoOpWithoutGlidersCopy_RefusedWhileGamesRun_AndSurfacesFailures()
    {
        using var rig = await StartedAsync();
        await rig.Service.RemoveOverlay();
        Assert.DoesNotContain("legendary.overlayRemove", rig.Calls.All);

        ManagedInstall(rig);
        rig.Service.OverlayForTests.Refresh();
        rig.Service.Hub.Mutate(s => s with { Sessions = s.Sessions.SetItem("G", new RunningGame("G", "G", RunState.Running, null, false, false)) });
        var running = await Assert.ThrowsAsync<GliderException>(() => rig.Service.RemoveOverlay());
        Assert.Equal(ErrorCode.GameRunning, running.Error.Code);

        rig.Service.Hub.Mutate(s => s with { Sessions = s.Sessions.Remove("G") });
        rig.Legendary.OverlayRemoveError = Errors.OverlayFailed("locked");
        var failed = await Assert.ThrowsAsync<GliderException>(() => rig.Service.RemoveOverlay());
        Assert.Equal(ErrorCode.OverlayFailed, failed.Error.Code);
        Assert.True((await rig.Service.GetSnapshot()).Overlay.Installed);
    }

    [Fact]
    public async Task ASecondInstallWhileOneRunsOrAnOffSwitchDuringItIsRefusedOrQueued()
    {
        string folder = "";
        using var rig = await StartedAsync(r => folder = ManagedInstall(r));
        await rig.Service.InstallOverlay();

        var on = await Assert.ThrowsAsync<GliderException>(() => rig.Service.SetOverlayEnabled(new GameFlagRequest(App, true)));
        var remove = await Assert.ThrowsAsync<GliderException>(() => rig.Service.RemoveOverlay());

        Assert.All(new[] { on, remove }, e => Assert.Equal(ErrorCode.Busy, e.Error.Code));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task AStaleVersionCacheTriggersOneMetadataLaneCheck_AndTheUpdateIsAnnouncedOnce()
    {
        using var rig = await StartedAsync(r => ManagedInstall(r, "1.0.0", latest: "1.0.0", checkedAt: r.Time.GetUtcNow().AddDays(-3)));
        rig.Legendary.OnSync = () => File.WriteAllText(Path.Combine(rig.Paths.LegendaryConfig, "overlay_version.json"),
            new JsonObject { ["last_update"] = rig.Time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0, ["data"] = new JsonObject { ["buildVersion"] = "1.5.0" } }.ToJsonString());
        var before = rig.Calls.Count("legendary.checkUpdates");

        await rig.Service.OverlayForTests.CheckOnceAsync(Ct);
        await rig.Service.OverlayForTests.CheckOnceAsync(Ct); // the cache is fresh now: no second spawn, no second notice

        Assert.Equal(before + 1, rig.Calls.Count("legendary.checkUpdates"));
        var state = (await rig.Service.GetSnapshot()).Overlay;
        Assert.Equal(("1.5.0", true), (state.LatestVersion, state.UpdateAvailable));
        Assert.Single(rig.Sink.Of<Notice>("notice"), n => n.Title == "Epic overlay update available");
    }

    [Fact]
    public async Task NoCheckWithoutAManagedOverlayOrASignIn()
    {
        using var rig = await StartedAsync();
        var before = rig.Calls.Count("legendary.checkUpdates");
        await rig.Service.OverlayForTests.CheckOnceAsync(Ct);
        Assert.Equal(before, rig.Calls.Count("legendary.checkUpdates"));

        ManagedInstall(rig, "1.0.0", latest: "1.0.0", checkedAt: rig.Time.GetUtcNow().AddDays(-3));
        rig.Service.Hub.Mutate(s => s with { Auth = new AuthState(AuthStatus.SignedOut, null) });
        await rig.Service.OverlayForTests.CheckOnceAsync(Ct);
        Assert.Equal(before, rig.Calls.Count("legendary.checkUpdates"));
    }
}
