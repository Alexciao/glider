using Glider.Core.Egl;

namespace Glider.Core.Tests.Egl;

public sealed class EglBusyTests
{
    private static string Bps(string game) => Path.Combine(game, ".egstore", "bps");

    private static void StartEglUpdate(string game)
    {
        Directory.CreateDirectory(Path.Combine(Bps(game), "FortniteGame", "Content", "Paks"));
        File.WriteAllText(Path.Combine(Bps(game), "FortniteGame", "Content", "Paks", "pakchunk0-WindowsClient.ucas"), "partial");
    }

    [Fact]
    public void LauncherRunningIsAProcessLookup()
    {
        using var t = new EglTestTree();
        using var svc = t.CreateService();
        Assert.False(svc.IsLauncherRunning());
        t.Processes.EglRunning = true;
        Assert.True(svc.IsLauncherRunning());
    }

    [Fact]
    public async Task OpenLauncherWithoutEvidenceIsNotBusy()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"), egstoreGuid: "11111111111111111111111111111111");
        t.WriteItem(game, guid: "11111111111111111111111111111111");
        t.Processes.EglRunning = true;
        using var svc = t.CreateService();

        Assert.False(svc.IsBusyWith(game));
        var state = await svc.ScanAsync(TestContext.Current.CancellationToken);
        Assert.True(state.LauncherRunning);
        Assert.False(state.BusyWithGame);
    }

    [Fact]
    public void StagingEvidenceCountsOnlyWhileEglRunsAndWhileFresh()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"));
        t.WriteItem(game);
        StartEglUpdate(game);
        using var svc = t.CreateService();

        Assert.False(svc.IsBusyWith(game)); // EGL closed: leftover staging is not "busy"
        t.Processes.EglRunning = true;
        Assert.True(svc.IsBusyWith(game));
        Assert.Equal(EglService.UpdatingReason, svc.GetBusyReason(game));
        Assert.True(svc.IsBusyWith(game.ToUpperInvariant() + "/"));

        EglTestTree.Age(Bps(game), TimeSpan.FromHours(2)); // paused/abandoned update
        Assert.False(svc.IsBusyWith(game));
    }

    [Fact]
    public void PendingFolderAndPendingManifestsAreEvidence()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"));
        t.WriteItem(game);
        t.Processes.EglRunning = true;
        using var svc = t.CreateService();

        var pending = Path.Combine(game, ".egstore", "Pending");
        Directory.CreateDirectory(pending);
        Assert.False(svc.IsBusyWith(game)); // empty Pending is not evidence
        File.WriteAllText(Path.Combine(pending, "next.manifest"), "x");
        Assert.True(svc.IsBusyWith(game));
        EglTestTree.Age(pending, TimeSpan.FromHours(1));
        Assert.False(svc.IsBusyWith(game));

        var pendingItem = t.WriteItem(game, EglTestTree.V3320, dir: Path.Combine(t.Environment.ManifestsDir, "Pending"));
        Assert.True(svc.IsBusyWith(game));
        EglTestTree.Age(pendingItem, TimeSpan.FromHours(1));
        Assert.False(svc.IsBusyWith(game));
    }

    [Fact]
    public void IncompleteInstallFlagIsEvidence()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"));
        var item = t.WriteItem(game, edit: j => j["bIsIncompleteInstall"] = true);
        EglTestTree.Age(item, TimeSpan.FromDays(3)); // flag counts regardless of age
        t.Processes.EglRunning = true;
        using var svc = t.CreateService();
        Assert.Equal(EglService.InstallingReason, svc.GetBusyReason(game));
    }

    [Fact]
    public async Task WaitBlocksUntilEvidenceClearsThenCompletes()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"), egstoreGuid: "11111111111111111111111111111111");
        t.WriteItem(game, guid: "11111111111111111111111111111111");
        StartEglUpdate(game);
        t.Processes.EglRunning = true;
        using var svc = t.CreateService();
        var reasons = new List<string?>();

        var wait = svc.WaitUntilNotBusyAsync(game, r => { lock (reasons) { reasons.Add(r); } }, TestContext.Current.CancellationToken);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);
        lock (reasons)
        {
            Assert.Equal([EglService.UpdatingReason], reasons);
        }

        Directory.Delete(Bps(game), recursive: true); // EGL finished its update
        await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        lock (reasons)
        {
            Assert.Equal([EglService.UpdatingReason, null], reasons);
        }
    }

    [Fact]
    public async Task WaitCompletesWhenLauncherExits()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"));
        t.WriteItem(game, edit: j => j["bIsIncompleteInstall"] = true);
        t.Processes.EglRunning = true;
        using var svc = t.CreateService();
        var blocked = new TaskCompletionSource<string?>();

        var wait = svc.WaitUntilNotBusyAsync(game, r => blocked.TrySetResult(r), TestContext.Current.CancellationToken);
        Assert.Equal(EglService.InstallingReason, await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        t.Processes.EglRunning = false;
        await wait.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitIsCancellableAndReturnsImmediatelyWhenNotBusy()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"));
        t.WriteItem(game);
        using var svc = t.CreateService();
        var calls = 0;
        await svc.WaitUntilNotBusyAsync(game, _ => calls++, TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);

        StartEglUpdate(game);
        t.Processes.EglRunning = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var wait = svc.WaitUntilNotBusyAsync(game, _ => { }, cts.Token);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task ReadOnlyOperationsLeaveEveryEglFileByteIdentical()
    {
        using var t = new EglTestTree();
        var a = t.CreateGame(t.Drive("C", "Fortnite"), egstoreGuid: "11111111111111111111111111111111");
        var b = t.CreateGame(t.Drive("E", "Fortnite"), egstoreGuid: "22222222222222222222222222222222");
        t.WriteItem(a, guid: "11111111111111111111111111111111");
        t.WriteItem(b, EglTestTree.V3320, "22222222222222222222222222222222", j => j["bIsIncompleteInstall"] = true);
        t.WriteItem(b, EglTestTree.V3320, dir: Path.Combine(t.Environment.ManifestsDir, "Pending"));
        t.WriteLauncherInstalled((a, EglTestTree.V3310, "Fortnite"));
        StartEglUpdate(b);
        t.Processes.EglRunning = true;
        var before = EglTreeHasher.Hash(t.Environment, a, b);
        using var svc = t.CreateService();

        await svc.ScanAsync(TestContext.Current.CancellationToken);
        svc.FindByLocation(a);
        svc.CheckDrift(Make.For(a, EglTestTree.V3300));
        svc.CheckDrift(Make.For(b, EglTestTree.V3310));
        svc.IsBusyWith(a);
        svc.IsBusyWith(b);
        svc.InspectForAdoption(a, "Fortnite");
        svc.InspectForAdoption(b, "Fortnite");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.WaitUntilNotBusyAsync(b, _ => { }, cts.Token));

        EglTreeHasher.AssertSame(before, EglTreeHasher.Hash(t.Environment, a, b));
        Assert.Empty(Directory.GetDirectories(t.Paths.EglBackups));
    }
}
