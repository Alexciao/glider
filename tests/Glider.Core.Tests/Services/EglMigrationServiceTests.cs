using Glider.Core.Contracts;
using Glider.Core.Egl;
using Glider.Core.Services;
using static Glider.Core.Tests.ManifestBuilder;

namespace Glider.Core.Tests.Services;

/// <summary>Adoption, bulk import results, readiness and write-back at the service level (fake Legendary/EGL).</summary>
public class EglMigrationServiceTests
{
    private const string Fn = FortniteLayout.AppName;

    private static async Task<Rig> ImportRig(string eglVersion, string latest, string recorded)
    {
        var rig = new Rig(latest: latest);
        var game = rig.MakeGameFolder();
        rig.Egl.Item = new EglItem("item", "guid-9", game, eglVersion, 100, false, false, Fn);
        rig.Egl.Scan = rig.Egl.Scan with
        {
            Installs = [new EglFoundInstall("egl-1", CandidateSource.EpicGamesLauncher, Fn, "Fortnite", game, eglVersion, 100, true, true, false, "guid-9")],
        };
        rig.Legendary.ImportVersion = recorded;
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        await rig.StartWarmAsync();
        return rig;
    }

    private static async Task<JobInfo> RunImport(Rig rig, JobInfo job)
    {
        await rig.Legendary.ImportAsync(job.AppName, job.TargetPath!, CancellationToken.None);
        return await rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);
    }

    /// <summary>A manifest for the rig's Fortnite folder: the exe (untagged), required paks present, optional paks not installed.</summary>
    private static void SeedManifest(Rig rig, string game, string version)
    {
        var files = new List<ManifestFile> { F(@"FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe", 1) };
        files.AddRange(Enumerable.Range(0, 4).Select(i => F($"FortniteGame/Content/Paks/req{i}.pak", 10, "Startup")));
        files.AddRange(Enumerable.Range(0, 9).Select(i => F($"FortniteGame/Content/Paks/opt{i}.pak", 10, "FortniteBROnDemand")));
        Touch(game, [.. Enumerable.Range(0, 4).Select(i => $"FortniteGame/Content/Paks/req{i}.pak")]);
        var path = Path.Combine(rig.Root, "manifests", version + ".manifest");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Build(version, files));
        rig.Legendary.ManifestPaths[(Fn, version)] = path;
    }

    [Fact]
    public async Task ImportCarriesTheInstallTagsTheFolderHolds_SoVerifyNeverCountsOptionalPacksAsMissing()
    {
        using var rig = await ImportRig("v5", "v5", "v5");
        SeedManifest(rig, rig.Egl.Item!.InstallLocation, "v5");
        var job = Assert.Single((await rig.Service.ImportEgl(new EglImportRequest(["egl-1"], VerifyMode.Auto))).Jobs);
        await RunImport(rig, job);

        var call = Assert.Single(rig.Legendary.TagCalls);
        Assert.Equal(Fn, call.App);
        Assert.Equal(["", "Startup"], call.Tags);
        Assert.True(call.Overwrite);
    }

    [Fact]
    public async Task EglsOwnInstallTagsAreCarriedToo()
    {
        using var rig = await ImportRig("v5", "v5", "v5");
        var game = rig.Egl.Item!.InstallLocation;
        rig.Egl.Item = rig.Egl.Item with { InstallTags = ["Startup", "FortniteBROnDemand"] };
        SeedManifest(rig, game, "v5");
        var job = Assert.Single((await rig.Service.ImportEgl(new EglImportRequest(["egl-1"], VerifyMode.Auto))).Jobs);
        await RunImport(rig, job);

        Assert.Equal(["", "FortniteBROnDemand", "Startup"], Assert.Single(rig.Legendary.TagCalls).Tags);
    }

    [Fact]
    public async Task AnUnreadableManifestLeavesTagsToLegendary_NeverFailsTheImport()
    {
        using var rig = await ImportRig("v5", "v5", "v5");
        var junk = Path.Combine(rig.Root, "junk.manifest");
        File.WriteAllText(junk, "not a manifest");
        rig.Legendary.ManifestPaths[(Fn, "v5")] = junk;
        var job = Assert.Single((await rig.Service.ImportEgl(new EglImportRequest(["egl-1"], VerifyMode.Auto))).Jobs);
        await RunImport(rig, job);

        Assert.Empty(rig.Legendary.TagCalls);
        Assert.NotNull(rig.Games.Record());
    }

    /// <summary>The 35 GB case as the service sees it: EGL left an update unfinished and there is no .mancpn, so Legendary recorded the latest build.</summary>
    [Fact]
    public async Task UnfinishedEglUpdate_IsReportedAsThatNotAsCorruption_AndVerifies()
    {
        using var rig = await ImportRig("v4", "v5", "v5");
        rig.Egl.Adoption = new EglAdoptionInfo(false, false, null, true, true, true, "v4", "v4", "v5", true);
        var job = Assert.Single((await rig.Service.ImportEgl(new EglImportRequest(["egl-1"], VerifyMode.Auto))).Jobs);
        await RunImport(rig, job);

        Assert.Equal([JobKind.Import, JobKind.Verify], rig.Jobs.Requests.Select(r => r.Kind));
        var note = rig.Sink.Of<Notice>("notice").Single(n => n.Title.Contains("imported", StringComparison.Ordinal));
        Assert.Contains("hadn't finished updating it to v5", note.Message);
        Assert.DoesNotContain("corrupt", note.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteInstallWithAllMetadata_IsImportedWithoutAnyVerify()
    {
        using var rig = await ImportRig("v5", "v5", "v5");
        rig.Egl.Adoption = new EglAdoptionInfo(true, true, "guid-9", false, false, false, "v5", "v5", null, false);
        var job = Assert.Single((await rig.Service.ImportEgl(new EglImportRequest(["egl-1"], VerifyMode.Auto))).Jobs);
        await RunImport(rig, job);
        Assert.Equal([JobKind.Import], rig.Jobs.Requests.Select(r => r.Kind));
        Assert.False(rig.Games.Record()!.NeedsVerification);
    }

    // ------------------------------------------------------------------ bulk import results

    [Fact]
    public async Task BulkImportReportsEverySkippedIdWithItsReason_AndStillQueuesTheRest()
    {
        using var rig = await ImportRig("v5", "v5", "v5");
        var folderB = rig.MakeOtherGameFolder("B");
        var folderC = rig.MakeOtherGameFolder("C");
        rig.Egl.Scan = rig.Egl.Scan with
        {
            Installs =
            [
                .. rig.Egl.Scan.Installs,
                new EglFoundInstall("egl-b", CandidateSource.EpicGamesLauncher, "not-mine", "Not Mine", folderB, "1", 1, true, true, false, "g-b"),
                new EglFoundInstall("egl-c", CandidateSource.EpicGamesLauncher, Fn + "2", "Gone", Path.Combine(rig.Root, "gone"), "1", 1, false, false, false, "g-c"),
                new EglFoundInstall("egl-d", CandidateSource.EpicGamesLauncher, "busy-app", "Busy One", folderC, "1", 1, true, true, true, "g-d"),
            ],
        };

        var result = await rig.Service.ImportEgl(new EglImportRequest(["egl-1", "egl-b", "egl-c", "egl-d", "nope", "egl-1"], VerifyMode.Auto));

        Assert.Equal(Fn, Assert.Single(result.Jobs).AppName);
        var skipped = result.Skipped!.ToDictionary(s => s.Id);
        Assert.Equal(["egl-b", "egl-c", "egl-d", "nope"], skipped.Keys.Order());
        Assert.Contains("isn't on your Epic account", skipped["egl-b"].Reason);
        Assert.Contains("doesn't exist", skipped["egl-c"].Reason);
        Assert.Contains("working on it", skipped["egl-d"].Reason);
        Assert.Equal("Busy One", skipped["egl-d"].Title);
        Assert.Null(skipped["nope"].AppName);
    }

    [Fact]
    public async Task ImportingTheSameBatchAgainIsIdempotent()
    {
        using var rig = await ImportRig("v5", "v5", "v5");
        var first = Assert.Single((await rig.Service.ImportEgl(new EglImportRequest(["egl-1"], VerifyMode.Auto))).Jobs);
        var again = Assert.Single((await rig.Service.ImportEgl(new EglImportRequest(["egl-1"], VerifyMode.Auto))).Jobs);
        Assert.Equal(first.Id, again.Id);
        Assert.Single(rig.Jobs.Requests);
    }

    [Fact]
    public async Task ValidateFolderReportsTheSize_FromEglWhenItKnowsElseFromTheFiles()
    {
        using var rig = await ImportRig("v5", "v5", "v5");
        var game = rig.Egl.Item!.InstallLocation;
        var v = await rig.Service.ValidateFolder(new ValidateFolderRequest(game));
        Assert.True(v.Valid);
        Assert.Equal(100, v.SizeBytes); // EGL's InstallSize

        rig.Egl.Item = null;
        File.WriteAllBytes(Path.Combine(game, "FortniteGame", "extra.bin"), new byte[1234]);
        v = await rig.Service.ValidateFolder(new ValidateFolderRequest(game));
        Assert.True(v.Valid);
        Assert.Equal(1234, v.SizeBytes); // the shipping exe is empty
    }

    // ------------------------------------------------------------------ readiness

    private static async Task<Rig> ReadyForReadiness(Action<Rig>? setup = null, bool linksOn = true)
    {
        var rig = new Rig();
        rig.Settings.Set(Settings.Default with { HandleEpicLinks = linksOn, CloudSaves = true, AutoUpdate = AutoUpdatePolicy.Never });
        setup?.Invoke(rig);
        await rig.StartWarmAsync();
        return rig;
    }

    [Fact]
    public async Task NothingDependsOnEgl_IsAClearAllClear()
    {
        using var rig = await ReadyForReadiness();
        // Fortnite not installed, no EGL data, links handled by Glider
        var r = await rig.Service.CheckEglReadiness();
        Assert.True(r.Ready);
        var item = Assert.Single(r.Items);
        Assert.Equal(ReadinessSeverity.Ok, item.Severity);
    }

    [Fact]
    public async Task GamesOnlyEglHasRegistered_BlockWithAnImportFix()
    {
        using var rig = await ReadyForReadiness(r =>
        {
            var game = r.MakeGameFolder();
            r.Egl.Scan = new EglScanResult(true, false, [new EglFoundInstall("egl-1", CandidateSource.EpicGamesLauncher, Fn, "Fortnite", game, "v2", 1, true, true, false, "g")], null);
        });
        var r = await rig.Service.CheckEglReadiness();
        Assert.False(r.Ready);
        var blocker = Assert.Single(r.Items, i => i.Severity == ReadinessSeverity.Blocker);
        Assert.Equal(Fn, blocker.AppName);
        Assert.Equal(ReadinessFix.Import, blocker.Fix);
    }

    [Fact]
    public async Task EglBusy_Blocks()
    {
        using var rig = await ReadyForReadiness(r =>
        {
            var game = r.MakeGameFolder();
            r.Egl.Scan = new EglScanResult(true, true, [new EglFoundInstall("egl-1", CandidateSource.EpicGamesLauncher, Fn, "Fortnite", game, "v2", 1, true, true, true, "g")], null);
        });
        var r = await rig.Service.CheckEglReadiness();
        Assert.False(r.Ready);
        Assert.Contains(r.Items, i => i.Severity == ReadinessSeverity.Blocker && i.Fix == ReadinessFix.WaitForEgl);
    }

    [Fact]
    public async Task WarningsDoNotBlock_AndNameTheirFix()
    {
        using var rig = await ReadyForReadiness(r =>
        {
            r.Install("v2", sharedWithEgl: true); // Fortnite: Easy Anti-Cheat, never launched through Glider
            r.EglOverlayInstalled = true;
            r.Egl.Pending.Add(new EglPendingUpdate(Fn, "v3", "x"));
            var cloud = LibraryFixture.Item("Cloud Game", "ns-c", "item-c", null, new() { ["CloudSaveFolder"] = "{AppData}/Cloud" });
            r.Library.Add("cloud-app", cloud, "1.0");
            var path = r.MakeOtherGameFolder("cloud-app");
            r.Legendary.Set("cloud-app", FakeLegendary.MakeInstalled(path, "1.0", "cloud-app", "Game.exe"));
            r.Games.Put("cloud-app", r.Games.Get("cloud-app") with { Install = new InstallRecord("cloud-app", path, "1.0", 1, DateTimeOffset.UnixEpoch, false, null, false) });
            r.Egl.Scan = new EglScanResult(true, false, [new EglFoundInstall("egl-1", CandidateSource.EpicGamesLauncher, Fn, "Fortnite", path + "-x", "v2", 1, true, true, false, "guid-1")], null);
        }, linksOn: false);
        await rig.SummaryAsync();
        var r = await rig.Service.CheckEglReadiness();

        Assert.True(r.Ready);
        Assert.DoesNotContain(r.Items, i => i.Severity == ReadinessSeverity.Blocker);
        Assert.Contains(r.Items, i => i.Fix == ReadinessFix.TurnOnEpicLinks);
        Assert.Contains(r.Items, i => i.Fix == ReadinessFix.InstallOverlay);
        Assert.Contains(r.Items, i => i.AppName == "cloud-app" && i.Fix == ReadinessFix.SyncSaves);
        Assert.Contains(r.Items, i => i.AppName == Fn && i.Fix == ReadinessFix.Launch && i.Message.Contains("Anti-Cheat", StringComparison.Ordinal));
        Assert.All(r.Items, i => Assert.NotEqual(ReadinessSeverity.Ok, i.Severity));
    }

    [Fact]
    public async Task AGameThatWasPlayedInGlider_NoLongerNeedsTheAnticheatNudge()
    {
        using var rig = await ReadyForReadiness(r =>
        {
            r.Install("v2");
            r.Games.Put(Fn, r.Games.Get(Fn) with { LastPlayedAt = DateTimeOffset.UnixEpoch });
        });
        await rig.SummaryAsync();
        var r = await rig.Service.CheckEglReadiness();
        Assert.DoesNotContain(r.Items, i => i.Message.Contains("Anti-Cheat", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ write-back opt-in

    [Fact]
    public async Task TurningSyncOn_WritesBackEveryGameGliderMovedPastEgl_ButNotBusyOnes()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { EglSync = false, AutoUpdate = AutoUpdatePolicy.Never });
        var rec = rig.Install("v2", sharedWithEgl: true);
        rig.Egl.Drift = new DriftResult(DriftKind.GliderNewer, "v1", "v2", rig.Egl.Item);
        await rig.StartWarmAsync();
        Assert.Empty(rig.Egl.WriteBacks); // sync off: nothing written at start

        rig.Egl.Busy = true;
        await rig.Service.UpdateSettings(Settings.Default with { EglSync = true, AutoUpdate = AutoUpdatePolicy.Never });
        await Task.Delay(300);
        Assert.Empty(rig.Egl.WriteBacks); // EGL is busy with it: never raced

        rig.Egl.Busy = false;
        await rig.Service.UpdateSettings(Settings.Default with { EglSync = false, AutoUpdate = AutoUpdatePolicy.Never });
        await rig.Service.UpdateSettings(Settings.Default with { EglSync = true, AutoUpdate = AutoUpdatePolicy.Never });
        await EndToEnd.EndToEndHarness.WaitFor(() => rig.Egl.WriteBacks.Count, n => n == 1, "the write-back after switching sync on");
        Assert.Equal(rec.InstallPath, rig.Egl.WriteBacks[0].InstallPath);
    }
}
