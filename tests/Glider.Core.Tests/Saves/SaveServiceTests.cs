using Glider.Core.Contracts;
using Glider.Core.Saves;
using Glider.Core.Tests.Services;

namespace Glider.Core.Tests.Saves;

public class SaveServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private const string App = SaveRig.App;

    // ------------------------------------------------------------------ first sync

    [Fact]
    public async Task FirstSync_LocalOnly_UploadsVerifiesAndRecordsTheLedger()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "level 3", T0);

        var state = await s.SyncAsync();

        Assert.Equal(SaveSyncStatus.InSync, state.Status);
        Assert.Equal(1, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Equal(0, s.Cloud.SyncCalls(SyncMode.Download));
        Assert.Equal(["list", "sync:Upload", "list"], s.Cloud.Calls); // listed before, uploaded, listed again to verify
        var ledger = (await s.LedgerAsync())!;
        Assert.Equal(["slot1.sav"], ledger.Files.Select(f => f.Path));
        Assert.Equal([CloudManifests.NameFor(T0)], ledger.Manifests);
        Assert.Equal(1, state.Backups); // a copy of what was uploaded
        Assert.Equal("level 3", File.ReadAllText(Directory.GetFiles(s.BackupsDir, "slot1.sav", SearchOption.AllDirectories).Single()));
        Assert.Equal(s.SaveDir, state.SavePath);
        Assert.Equal(SaveSyncStatus.InSync, (await s.SummaryAsync()).SaveStatus);
    }

    [Fact]
    public async Task FirstSync_CloudOnly_Downloads()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Cloud.AddSave(T0, ("slot1.sav", "cloud progress"));

        var state = await s.SyncAsync();

        Assert.Equal(SaveSyncStatus.InSync, state.Status);
        Assert.Equal("cloud progress", s.Read("slot1.sav"));
        Assert.Equal(0, state.Backups); // nothing local to back up
        Assert.Equal(1, s.Cloud.SyncCalls(SyncMode.Download));
        Assert.Equal([CloudManifests.NameFor(T0)], (await s.LedgerAsync())!.Manifests);
    }

    [Fact]
    public async Task FirstSync_BothSidesDiffer_IsAConflict_AndNothingIsTouched()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "local", T0);
        s.Cloud.AddSave(T0.AddDays(5), ("slot1.sav", "cloud")); // the cloud looks newer, but Glider can't tell which holds the progress

        var state = await s.SyncAsync();

        Assert.Equal(SaveSyncStatus.Conflict, state.Status);
        Assert.NotNull(state.Conflict);
        Assert.Equal(T0, state.Conflict!.LocalModifiedAt);
        Assert.Equal(T0.AddDays(5), state.Conflict.CloudModifiedAt);
        Assert.Null(state.Conflict.LastSyncedAt); // never synced through Glider
        Assert.Equal("local", s.Read("slot1.sav"));
        Assert.Equal(0, s.Cloud.SyncCalls(SyncMode.Upload) + s.Cloud.SyncCalls(SyncMode.Download));
        Assert.Equal(0, state.Backups);
        Assert.Equal(SaveSyncStatus.Conflict, (await s.SummaryAsync()).SaveStatus); // the library delta carries it
        Assert.Null(await s.LedgerAsync());
    }

    [Fact]
    public async Task FirstSync_BothSidesCarryTheSameMoment_IsInSync_WithoutTransfers()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "same", T0.AddSeconds(20));
        s.Cloud.AddSave(T0, ("slot1.sav", "same"));

        var state = await s.SyncAsync();

        Assert.Equal(SaveSyncStatus.InSync, state.Status);
        Assert.Equal(0, s.Cloud.SyncCalls(SyncMode.Upload) + s.Cloud.SyncCalls(SyncMode.Download));
        Assert.NotNull(await s.LedgerAsync());
    }

    // ------------------------------------------------------------------ later syncs: the right direction, from the ledger

    [Fact]
    public async Task CloudChanged_LocalUntouched_Downloads_AfterABackupOfTheLocalSave()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync(); // upload: baseline
        s.Cloud.AddSave(T0.AddHours(5), ("slot1.sav", "v2 from my laptop"));

        // The backup of the local save must exist, and be complete, before Legendary gets to delete anything.
        string? seenInBackup = null;
        s.Cloud.BeforeSync = mode =>
        {
            if (mode == SyncMode.Download)
            {
                seenInBackup = Directory.GetDirectories(s.BackupsDir).Select(d => Path.Combine(d, "files", "slot1.sav")).Where(File.Exists).Select(File.ReadAllText).FirstOrDefault(t => t == "v1");
            }
        };

        var state = await s.SyncAsync();

        Assert.Equal("v1", seenInBackup);
        Assert.Equal("v2 from my laptop", s.Read("slot1.sav"));
        Assert.Equal(SaveSyncStatus.InSync, state.Status);
        Assert.Equal(2, state.Backups); // the upload copy and the pre-download backup
        Assert.Equal(CloudManifests.NameFor(T0.AddHours(5)), (await s.LedgerAsync())!.Manifests.Order().Last());
    }

    [Fact]
    public async Task LocalChanged_CloudUntouched_Uploads_AndTheCloudKeepsItsOlderSave()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync();
        s.Write("slot1.sav", "v2 played on this PC", T0.AddHours(3));

        var state = await s.SyncAsync();

        Assert.Equal(SaveSyncStatus.InSync, state.Status);
        Assert.Equal(2, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Equal(2, s.Cloud.Saves.Count); // uploading never removes the previous cloud save
        Assert.Equal("v2 played on this PC", s.Read("slot1.sav"));
    }

    [Fact]
    public async Task NeitherChanged_DoesNothing()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync();
        s.Cloud.Calls.Clear();

        var state = await s.SyncAsync();

        Assert.Equal(SaveSyncStatus.InSync, state.Status);
        Assert.Equal(["list"], s.Cloud.Calls);
    }

    [Fact]
    public async Task BothChanged_IsAConflict_NothingIsOverwritten_AndAutoNeverResolvesIt()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync();
        s.Write("slot1.sav", "local v2", T0.AddHours(1));
        s.Cloud.AddSave(T0.AddHours(8), ("slot1.sav", "cloud v2"));
        var before = s.Cloud.Calls.Count(c => c.StartsWith("sync", StringComparison.Ordinal));

        var again = await s.SyncAsync();
        var third = await s.SyncAsync();

        Assert.Equal(SaveSyncStatus.Conflict, again.Status);
        Assert.Equal(SaveSyncStatus.Conflict, third.Status);
        Assert.Equal("local v2", s.Read("slot1.sav"));
        Assert.Equal(before, s.Cloud.Calls.Count(c => c.StartsWith("sync", StringComparison.Ordinal)));
        Assert.NotNull(again.Conflict!.LastSyncedAt);
        Assert.True(again.Conflict.LocalSizeBytes > 0);
        Assert.Equal(SaveSyncStatus.Conflict, (await s.SummaryAsync()).SaveStatus);
    }

    [Fact]
    public async Task ClockSkew_ALocalChangeWithAnOlderMtimeIsStillUploaded_NeverReplacedByTheCloud()
    {
        // Legendary compares clocks: the cloud head (T0) is newer than this save's mtime (two days before), so it would download
        // and delete the progress made here. The 3-way check sees that only the local side changed.
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        s.Cloud.AddSave(T0, ("slot1.sav", "v1"));
        await s.SeedLedgerAsync(T0);
        s.Write("slot1.sav", "progress from a PC with a slow clock", T0.AddDays(-2));

        var state = await s.SyncAsync();

        Assert.Equal(SaveSyncStatus.InSync, state.Status);
        Assert.Equal(1, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Equal(0, s.Cloud.SyncCalls(SyncMode.Download));
        Assert.Equal("progress from a PC with a slow clock", s.Read("slot1.sav"));
    }

    [Fact]
    public async Task ClockSkew_ACloudChangeNamedBeforeTheLocalMtimeIsStillDownloaded()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0.AddDays(30)); // this PC's clock is far ahead
        s.Cloud.AddSave(T0, ("slot1.sav", "v1"));
        await s.SeedLedgerAsync(T0);
        s.Cloud.AddSave(T0.AddDays(1), ("slot1.sav", "new progress"));

        await s.SyncAsync();

        Assert.Equal("new progress", s.Read("slot1.sav"));
        Assert.Equal(1, s.Cloud.SyncCalls(SyncMode.Download));
    }

    [Fact]
    public async Task ADeletedLocalFile_IsALocalChange_AndTheRemainingSaveIsUploaded()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "a", T0);
        s.Write("slot2.sav", "b", T0);
        s.Cloud.AddSave(T0, ("slot1.sav", "a"), ("slot2.sav", "b"));
        await s.SeedLedgerAsync(T0);
        File.Delete(Path.Combine(s.SaveDir, "slot2.sav"));

        await s.SyncAsync();

        Assert.Equal(1, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Equal(["slot1.sav"], s.Cloud.Saves[^1].Files.Keys.Order());
    }

    [Fact]
    public async Task AnEmptiedSaveFolder_IsRestoredFromTheCloud_WithNothingToLose()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "a", T0);
        s.Cloud.AddSave(T0, ("slot1.sav", "a"));
        await s.SeedLedgerAsync(T0);
        File.Delete(Path.Combine(s.SaveDir, "slot1.sav"));

        await s.SyncAsync();

        Assert.Equal("a", s.Read("slot1.sav"));
    }

    [Fact]
    public async Task OnlyFilesThatTheGamesListMatchesCountAsChanges()
    {
        using var s = new SaveRig(include: "*.sav", exclude: "tmp/");
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        s.Write("debug.log", "boot", T0);
        s.Write("tmp/scratch.sav", "x", T0);
        s.Cloud.AddSave(T0, ("slot1.sav", "v1"));
        await s.SeedLedgerAsync(T0);
        s.Write("debug.log", "boot again, much longer", T0.AddHours(9)); // a log written on every start: Legendary would call local "newer"
        s.Write("tmp/scratch.sav", "y", T0.AddHours(9));

        var state = await s.SyncAsync();

        Assert.Equal(SaveSyncStatus.InSync, state.Status);
        Assert.Equal(0, s.Cloud.SyncCalls(SyncMode.Upload));
    }

    [Fact]
    public async Task ADownloadOnlyBacksUpAndReplacesTheFilteredFiles()
    {
        using var s = new SaveRig(include: "*.sav");
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        s.Write("settings.ini", "keep me", T0);
        s.Cloud.AddSave(T0, ("slot1.sav", "v1"));
        await s.SeedLedgerAsync(T0);
        s.Cloud.AddSave(T0.AddHours(1), ("slot1.sav", "v2"));

        await s.SyncAsync();

        Assert.Equal("v2", s.Read("slot1.sav"));
        Assert.Equal("keep me", s.Read("settings.ini"));
        var overwritten = Directory.GetDirectories(s.BackupsDir).Select(d => Path.Combine(d, "files")).Where(d => File.Exists(Path.Combine(d, "slot1.sav")) && File.ReadAllText(Path.Combine(d, "slot1.sav")) == "v1").ToList();
        Assert.NotEmpty(overwritten);
        Assert.All(overwritten, d => Assert.False(File.Exists(Path.Combine(d, "settings.ini"))));
    }

    // ------------------------------------------------------------------ backups before overwrites; failures

    private static async Task<SaveRig> CloudNewerRig(DownloadBehavior behavior, SaveOptions? options = null)
    {
        var s = new SaveRig(options: options);
        await s.StartAsync();
        s.Write("slot1.sav", "original 1", T0);
        s.Write("slot2.sav", "original 2", T0);
        s.Cloud.AddSave(T0, ("slot1.sav", "original 1"), ("slot2.sav", "original 2"));
        await s.SeedLedgerAsync(T0);
        s.Cloud.AddSave(T0.AddHours(4), ("a.sav", "cloud a"), ("b.sav", "cloud b"));
        s.Cloud.Download = behavior;
        return s;
    }

    [Theory]
    [InlineData("FailsAfterDelete")]
    [InlineData("PartialWrite")]
    public async Task AFailedDownload_PutsTheOriginalBack_KeepsTheBackup_AndLeavesTheLedgerAlone(string behavior)
    {
        using var s = await CloudNewerRig(Enum.Parse<DownloadBehavior>(behavior));
        var ledgerBefore = (await s.LedgerAsync())!;

        var e = await Assert.ThrowsAsync<GliderException>(() => s.SyncAsync());

        Assert.Equal(ErrorCode.SaveSyncFailed, e.Error.Code);
        Assert.Equal("original 1", s.Read("slot1.sav"));
        Assert.Equal("original 2", s.Read("slot2.sav"));
        Assert.Null(s.Read("a.sav")); // what the half-finished download wrote is removed
        Assert.Equal(["slot1.sav", "slot2.sav"], s.Files().Order());
        Assert.Equal(1, s.State().Backups);
        Assert.Equal(ledgerBefore, await s.LedgerAsync());
        Assert.Equal(SaveSyncStatus.Error, s.State().Status);
        Assert.NotNull(s.State().LastError);
    }

    [Fact]
    public async Task AKilledDownload_IsRestoredToo_AndTheCancellationPropagates()
    {
        using var s = await CloudNewerRig(DownloadBehavior.KilledAfterDelete);

        var e = await Assert.ThrowsAsync<GliderException>(() => s.SyncAsync());

        Assert.Equal(ErrorCode.Cancelled, e.Error.Code);
        Assert.Equal("original 1", s.Read("slot1.sav"));
        Assert.Equal("original 2", s.Read("slot2.sav"));
        Assert.Equal(1, s.State().Backups);
    }

    [Fact]
    public async Task IfTheBackupCannotBeMade_NothingIsOverwritten()
    {
        using var s = await CloudNewerRig(DownloadBehavior.Normal, new SaveOptions { PostExitRetryDelays = [], Backups = new SaveBackupOptions { MaxBackupBytes = 1 } });

        var e = await Assert.ThrowsAsync<GliderException>(() => s.SyncAsync());

        Assert.Equal(ErrorCode.SaveSyncFailed, e.Error.Code);
        Assert.Contains("backed up", e.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, s.Cloud.SyncCalls(SyncMode.Download));
        Assert.Equal("original 1", s.Read("slot1.sav"));
        Assert.Equal(0, s.State().Backups);
    }

    [Fact]
    public async Task AnUploadThatNeverLanded_IsAnError_NotASuccess()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        s.Cloud.DropUploads = true; // Legendary prints "Finished uploading" even when the PUT failed

        var e = await Assert.ThrowsAsync<GliderException>(() => s.SyncAsync());

        Assert.Equal(ErrorCode.SaveSyncFailed, e.Error.Code);
        Assert.Contains("didn't confirm", e.Error.Message, StringComparison.Ordinal);
        Assert.Null(await s.LedgerAsync()); // not recorded as synced
        Assert.Equal("v1", s.Read("slot1.sav"));
        Assert.Equal(SaveSyncStatus.Error, (await s.SummaryAsync()).SaveStatus);

        s.Cloud.DropUploads = false; // the next sync retries because the ledger still says "never synced"
        Assert.Equal(SaveSyncStatus.InSync, (await s.SyncAsync()).Status);
    }

    [Fact]
    public async Task ExplicitDirections_AreTheUsersChoice_ButStillBackUpBeforeOverwriting()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "local", T0);
        s.Cloud.AddSave(T0.AddDays(2), ("slot1.sav", "cloud"));

        var down = await s.SyncAsync(SyncDirection.Download);

        Assert.Equal("cloud", s.Read("slot1.sav"));
        Assert.Equal(SaveSyncStatus.InSync, down.Status);
        Assert.Equal(1, down.Backups);
        Assert.Equal("local", File.ReadAllText(Directory.GetFiles(s.BackupsDir, "slot1.sav", SearchOption.AllDirectories).Single()));
    }

    // ------------------------------------------------------------------ resolving a conflict

    private static async Task<SaveRig> ConflictRig()
    {
        var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync();
        s.Write("slot1.sav", "LOCAL v2", T0.AddHours(1));
        s.Cloud.AddSave(T0.AddHours(8), ("slot1.sav", "CLOUD v2"));
        Assert.Equal(SaveSyncStatus.Conflict, (await s.SyncAsync()).Status);
        return s;
    }

    [Fact]
    public async Task ResolveKeepLocal_UploadsIt_AndTheCloudsOtherSaveStaysInTheCloud()
    {
        using var s = await ConflictRig();

        var result = await s.Service.ResolveSaves(new SaveResolveRequest(App, SaveSide.Local, false));

        Assert.Null(result);
        Assert.Equal("LOCAL v2", s.Read("slot1.sav"));
        Assert.Equal(SaveSyncStatus.InSync, s.State().Status);
        Assert.Null(s.State().Conflict);
        Assert.Equal(3, s.Cloud.Saves.Count); // v1, the other PC's v2, and now this PC's
        Assert.Equal("LOCAL v2", File.ReadAllText(Directory.GetFiles(s.BackupsDir, "slot1.sav", SearchOption.AllDirectories).Select(f => (f, File.ReadAllText(f))).First(x => x.Item2 == "LOCAL v2").f));
    }

    [Fact]
    public async Task ResolveKeepCloud_BacksUpTheLocalSave_ThenDownloads()
    {
        using var s = await ConflictRig();

        await s.Service.ResolveSaves(new SaveResolveRequest(App, SaveSide.Cloud, false));

        Assert.Equal("CLOUD v2", s.Read("slot1.sav"));
        Assert.Equal(SaveSyncStatus.InSync, s.State().Status);
        var backups = Directory.GetFiles(s.BackupsDir, "slot1.sav", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
        Assert.Contains("LOCAL v2", backups); // the save that lost is not gone
    }

    [Fact]
    public async Task ResolveThenLaunch_StartsTheGameWithoutSyncingAgain()
    {
        using var s = await ConflictRig();
        var calls = s.Cloud.Calls.Count;

        var result = await s.Service.ResolveSaves(new SaveResolveRequest(App, SaveSide.Local, ThenLaunch: true));

        Assert.Equal(LaunchOutcome.Started, result!.Outcome);
        Assert.Single(s.Rig.Processes.Started);
        Assert.Equal(["sync:Upload", "list"], s.Cloud.Calls.Skip(calls + 1).ToList()); // list + upload + verify only; the launch added nothing
    }

    // ------------------------------------------------------------------ launch

    [Fact]
    public async Task LaunchIsBlockedByAConflict_NothingStarts_NothingIsOverwritten()
    {
        using var s = await ConflictRig();
        var calls = s.Cloud.Calls.Count;

        var result = await s.Service.Launch(new LaunchRequest(App, null, false));

        Assert.Equal(LaunchOutcome.SaveConflict, result.Outcome);
        Assert.Equal(App, result.Conflict!.AppName);
        Assert.Equal("Save Game", result.Conflict.Title);
        Assert.NotNull(result.Conflict.LastSyncedAt);
        Assert.Empty(s.Rig.Processes.Started);
        Assert.Equal(RunState.Idle, s.Service.Hub.State.RunStateOf(App));
        Assert.Equal("LOCAL v2", s.Read("slot1.sav"));
        Assert.DoesNotContain(s.Cloud.Calls.Skip(calls), c => c.StartsWith("sync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlayWithoutSyncing_SkipsTheSyncOnce()
    {
        using var s = await ConflictRig();
        var calls = s.Cloud.Calls.Count;

        var result = await s.Service.Launch(new LaunchRequest(App, null, SkipSaveSync: true));

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal(calls, s.Cloud.Calls.Count);
    }

    [Fact]
    public async Task LaunchDownloadsANewerCloudSave_WhileShowingSyncingSaves()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        s.Cloud.AddSave(T0, ("slot1.sav", "v1"));
        await s.SeedLedgerAsync(T0);
        s.Cloud.AddSave(T0.AddHours(6), ("slot1.sav", "v2 from the other PC"));
        var states = new List<RunState>();
        s.Service.Hub.StateChanged += st =>
        {
            lock (states)
            {
                var run = st.RunStateOf(App);
                if (states.Count == 0 ? run != RunState.Idle : states[^1] != run)
                {
                    states.Add(run);
                }
            }
        };

        var result = await s.Service.Launch(new LaunchRequest(App, null, false));

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal("v2 from the other PC", s.Read("slot1.sav"));
        Assert.Contains(RunState.SyncingSaves, states);
        Assert.True(states.IndexOf(RunState.SyncingSaves) < states.IndexOf(RunState.Launching));
    }

    [Fact]
    public async Task LaunchOffline_SkipsTheSyncWithAQuietNote_AndNeverBlocksPlay()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        s.Cloud.AddSave(T0.AddDays(2), ("slot1.sav", "cloud"));
        s.Service.Hub.Mutate(st => st with { Connectivity = Connectivity.Offline });

        var result = await s.Service.Launch(new LaunchRequest(App, null, false));

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.True(result.Offline);
        Assert.Empty(s.Cloud.Calls);
        Assert.Equal("v1", s.Read("slot1.sav"));
        Assert.Contains("Offline", s.State().Note, StringComparison.Ordinal);
        Assert.Null(s.State().LastError);
        Assert.DoesNotContain(s.Notices(), n => n.Level != NoticeLevel.Info);
    }

    [Fact]
    public async Task LaunchWhenEpicIsUnreachable_PlaysWithoutAnError()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        s.Cloud.ListError = new GliderException(Errors.Network("timed out"));

        var result = await s.Service.Launch(new LaunchRequest(App, null, false));

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal(0, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Contains("sync the next time", s.State().Note, StringComparison.Ordinal);
        Assert.Null(s.State().LastError);
    }

    [Fact]
    public async Task ASyncProblemBeforeLaunch_ShowsANotice_ButTheGameStarts()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        s.Cloud.DropUploads = true;

        var result = await s.Service.Launch(new LaunchRequest(App, null, false));

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Contains(s.Notices(), n => n.Level == NoticeLevel.Warning && n.AppName == App);
        Assert.Equal("v1", s.Read("slot1.sav"));
    }

    [Fact]
    public async Task ASlowPreLaunchSync_IsAbandonedAfterTheTimeout_AndTheRestoreRuns()
    {
        using var s = new SaveRig(options: new SaveOptions { PostExitRetryDelays = [], PreLaunchTimeout = TimeSpan.FromMilliseconds(500) });
        await s.StartAsync();
        s.Write("slot1.sav", "original", T0);
        s.Cloud.AddSave(T0, ("slot1.sav", "original"));
        await s.SeedLedgerAsync(T0);
        s.Cloud.AddSave(T0.AddHours(2), ("slot1.sav", "newer"));
        s.Cloud.SyncGate = new TaskCompletionSource(); // the download hangs

        var launch = s.Service.Launch(new LaunchRequest(App, null, false));
        await WaitUntil(() => s.Cloud.SyncCalls(SyncMode.Download) == 1);
        s.Rig.Time.Advance(TimeSpan.FromSeconds(1)); // the pre-launch timer runs on the service's clock
        var result = await launch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal("original", s.Read("slot1.sav"));
        Assert.Contains("too long", result.Message ?? s.State().Note, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ after exit

    [Fact]
    public async Task AfterTheGameCloses_ChangedSavesAreUploaded()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync();
        var uploads = s.Cloud.SyncCalls(SyncMode.Upload);

        await s.LaunchAsync(whilePlaying: () => s.Write("slot1.sav", "progress made while playing", T0.AddHours(1)));
        await s.Saves.PostExitIdle;

        Assert.Equal(uploads + 1, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Equal(SaveSyncStatus.InSync, s.State().Status);
        Assert.Equal("progress made while playing", s.Read("slot1.sav"));
    }

    [Fact]
    public async Task AFailedUploadAfterExit_ShowsANotice_AndNeverDeletesAnything()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync();
        s.Cloud.DropUploads = true;
        s.Write("slot1.sav", "unsynced progress", T0.AddHours(1));

        s.Saves.AfterExit(App);
        var idle = s.Saves.PostExitIdle;
        await idle;
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.True(s.Notices().Any(n => n.Level == NoticeLevel.Warning && n.Title.Contains("upload", StringComparison.OrdinalIgnoreCase)), string.Join(" / ", s.Rig.Log.Lines.Where(l => l.Contains("[saves]", StringComparison.Ordinal))));
        Assert.Equal("unsynced progress", s.Read("slot1.sav"));
        Assert.Equal(SaveSyncStatus.Error, s.State().Status);
    }

    [Fact]
    public async Task AfterExit_RetriesLater_AndSucceedsQuietly()
    {
        using var s = new SaveRig(options: new SaveOptions { PostExitRetryDelays = [TimeSpan.FromSeconds(30)] });
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync();
        s.Cloud.DropUploads = true;
        s.Write("slot1.sav", "v2", T0.AddHours(1));

        s.Saves.AfterExit(App);
        await WaitUntil(() => s.Cloud.SyncCalls(SyncMode.Upload) == 2); // 1 baseline + the first attempt
        s.Cloud.DropUploads = false;
        s.Rig.Time.Advance(TimeSpan.FromSeconds(31));
        await s.Saves.PostExitIdle;

        Assert.Equal(3, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Equal(SaveSyncStatus.InSync, s.State().Status);
        Assert.DoesNotContain(s.Notices(), n => n.Level == NoticeLevel.Warning);
    }

    [Fact]
    public async Task AfterExitWithAConflict_NotifiesAndOffersToResolve()
    {
        using var s = await ConflictRig();

        s.Saves.AfterExit(App);
        await s.Saves.PostExitIdle;

        var notice = Assert.Single(s.Notices(), n => n.Action == FixAction.ResolveSaves);
        Assert.Equal(App, notice.AppName);
        Assert.Equal("LOCAL v2", s.Read("slot1.sav"));
    }

    // ------------------------------------------------------------------ switches and refusals

    [Fact]
    public async Task ThePerGameSwitchAndTheGlobalSwitch_TurnEverythingOff()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);

        await s.Rig.Games.UpdateAsync(App, e => e with { Settings = e.Settings with { CloudSaves = false } });
        var perGame = await s.SyncAsync();
        await s.Service.Launch(new LaunchRequest(App, null, false));
        Assert.Equal(SaveSyncStatus.Disabled, perGame.Status);
        Assert.False(perGame.Enabled);
        Assert.True(perGame.Supported);
        Assert.Empty(s.Cloud.Calls);
        Assert.Equal(SaveSyncStatus.Disabled, (await s.SummaryAsync()).SaveStatus);

        await s.Rig.Games.UpdateAsync(App, e => e with { Settings = e.Settings with { CloudSaves = true } });
        s.Rig.Settings.Set(s.Rig.Settings.Current with { CloudSaves = false });
        Assert.Equal(SaveSyncStatus.Disabled, (await s.SyncAsync()).Status);
        Assert.Empty(s.Cloud.Calls);
    }

    [Fact]
    public async Task AGameWithoutCloudSaves_IsRefused()
    {
        using var s = new SaveRig();
        s.Rig.AddGame("NoCloud", "No Cloud");
        await s.StartAsync();

        var e = await Assert.ThrowsAsync<GliderException>(() => s.Service.SyncSaves(new SaveSyncRequest("NoCloud", SyncDirection.Auto)));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
        Assert.False((await s.Service.GetSaves(new AppRef("NoCloud"))).Supported);
        Assert.Equal(SaveSyncStatus.NotSupported, (await s.Service.GetSaves(new AppRef("NoCloud"))).Status);
    }

    [Fact]
    public async Task ASyncIsRefusedWhileTheGameRuns_AndWhileAWriterJobTouchesIt()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.Service.Launch(new LaunchRequest(App, null, SkipSaveSync: true));

        var running = await Assert.ThrowsAsync<GliderException>(() => s.SyncAsync());
        Assert.Equal(ErrorCode.GameRunning, running.Error.Code);

        s.Service.LaunchServiceForTests.Sessions.Abort(App);
        s.Service.Hub.Mutate(st => st with { Sessions = st.Sessions.Remove(App) });
        s.Rig.Jobs.Enqueue(new JobRequest(App, JobKind.Update, null, null, "1.0", "2.0", "Save Game"));
        var busy = await Assert.ThrowsAsync<GliderException>(() => s.SyncAsync());
        Assert.Equal(ErrorCode.GameBusy, busy.Error.Code);
        Assert.Empty(s.Cloud.Calls);
        Assert.Equal("v1", s.Read("slot1.sav"));
    }

    [Fact]
    public async Task OnlyOneSyncRunsAtATime_TheSavesLane()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        s.Cloud.SyncGate = new TaskCompletionSource();

        var first = s.SyncAsync();
        await WaitUntil(() => s.Cloud.SyncCalls(SyncMode.Upload) == 1);
        var second = s.SyncAsync();
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(["list", "sync:Upload"], s.Cloud.Calls); // the second one has not even listed
        Assert.False(second.IsCompleted);

        s.Cloud.SyncGate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, s.Cloud.SyncCalls(SyncMode.Upload)); // the second found everything in sync
        Assert.Equal(SaveSyncStatus.InSync, s.State().Status);
    }

    [Fact]
    public async Task AWriterJobWaitsForASaveSyncOfItsGame()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        s.Cloud.SyncGate = new TaskCompletionSource();
        var sync = s.SyncAsync();
        await WaitUntil(() => s.Cloud.SyncCalls(SyncMode.Upload) == 1);

        var wait = s.Saves.WhenIdleAsync(App, TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);
        Assert.True(s.Saves.WhenIdleAsync("SomeOtherGame", TestContext.Current.CancellationToken).IsCompleted);

        s.Cloud.SyncGate.SetResult();
        await sync;
        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // ------------------------------------------------------------------ first-time save folder

    [Fact]
    public async Task TheSaveFolderIsSetUpByLegendaryOnce_WhenNoWriterJobRuns()
    {
        using var s = new SaveRig();
        await s.StartAsync(pathStored: false);
        s.Write("slot1.sav", "v1", T0);

        var state = await s.SyncAsync();

        Assert.Equal("sync:SetupPath", s.Cloud.Calls[0]);
        Assert.Equal(s.SaveDir, state.SavePath);
        Assert.Equal(SaveSyncStatus.InSync, state.Status);

        s.Cloud.Calls.Clear();
        await s.SyncAsync();
        Assert.DoesNotContain("sync:SetupPath", s.Cloud.Calls); // stored: later syncs never touch installed.json
    }

    [Fact]
    public async Task WhileAnotherGameDownloads_TheSetupWaits_ManualSaysSo_AndTheLaunchStillStarts()
    {
        using var s = new SaveRig();
        s.Rig.AddGame("Other", "Other Game");
        await s.StartAsync(pathStored: false);
        var job = s.Rig.Jobs.Enqueue(new JobRequest("Other", JobKind.Install, "C:/Games", null, null, "1.0", "Other Game"));
        s.Rig.Jobs.Set(s.Rig.Jobs.Get(job.Id) with { Status = JobStatus.Running });
        s.Write("slot1.sav", "v1", T0);

        var manual = await Assert.ThrowsAsync<GliderException>(() => s.SyncAsync());
        Assert.Equal(ErrorCode.Busy, manual.Error.Code);

        var result = await s.Service.Launch(new LaunchRequest(App, null, false));
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Empty(s.Cloud.Calls); // no sync-saves while a writer job holds installed.json
        Assert.Contains("download", s.State().Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASaveFolderThatIsTheInstallFolder_IsRefused_BecauseADownloadWouldClearTheGame()
    {
        using var s = new SaveRig();
        await s.StartAsync(pathStored: false);
        s.Cloud.SavePath = s.InstallPath; // CloudSaveFolder = {installdir} with no include list
        s.Cloud.AddSave(T0, ("slot1.sav", "x"));

        var e = await Assert.ThrowsAsync<GliderException>(() => s.SyncAsync());

        Assert.Equal(ErrorCode.SaveSyncFailed, e.Error.Code);
        Assert.Equal(0, s.Cloud.SyncCalls(SyncMode.Download));
        Assert.True(File.Exists(Path.Combine(s.InstallPath, "Game.exe")));
    }

    [Fact]
    public async Task AChangedSaveFolder_StartsOverAsAFirstSync()
    {
        using var s = new SaveRig();
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync();
        var elsewhere = Path.Combine(s.Rig.Root, "moved-saves");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "slot1.sav"), "different");
        File.SetLastWriteTimeUtc(Path.Combine(elsewhere, "slot1.sav"), T0.AddDays(9).UtcDateTime);
        s.Cloud.SavePath = elsewhere;
        s.Cloud.StoreSavePath(elsewhere);

        var state = await s.SyncAsync();

        Assert.Equal(SaveSyncStatus.Conflict, state.Status); // not "local changed → upload" against a ledger of another folder
    }

    [Fact]
    public async Task OpenBackups_OpensTheGamesBackupFolder()
    {
        using var s = new SaveRig();
        await s.StartAsync();

        await s.Service.OpenSaveBackups(new AppRef(App));

        Assert.Contains(s.BackupsDir, s.Rig.Host.Opened);
        Assert.True(Directory.Exists(s.BackupsDir));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition never became true");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
