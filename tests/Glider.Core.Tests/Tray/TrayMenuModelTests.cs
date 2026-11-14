using Glider.Core.Contracts;
using Glider.Core.Tray;

namespace Glider.Core.Tests.Tray;

public class TrayMenuModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static GameSummary Game(string app, string title, DateTimeOffset? played = null, InstallStatus status = InstallStatus.Installed,
        GameKind kind = GameKind.Game, bool hidden = false) => new(
        app, title, kind, null, "ns", "id-" + app, null, status, "1", "1", false, @"C:\Games\" + app, null, null, null,
        false, SaveSyncStatus.NotSupported, true, ThirdPartyStore.None, null, 0, false, hidden, null, played, 0,
        new GameArt(null, null, null, null, null), RunState.Idle, null, null, false, false);

    private static JobInfo Job(string id, string app, string title, JobStatus status, JobKind kind = JobKind.Install, JobPhase? phase = null,
        double? percent = null, string? blocked = null) => new(
        id, app, title, kind, status, [], phase,
        percent is null ? null : new JobProgress(id, app, phase ?? JobPhase.Downloading, percent.Value, percent.Value, 1, null, null, null, null, null, null, DownloadMode.Balanced, Limiter.Network),
        null, Now, null, null, null, null, null, blocked, null, null);

    private static AppSnapshot Snapshot(
        IReadOnlyList<JobInfo>? jobs = null,
        bool paused = false,
        DownloadMode setting = DownloadMode.Balanced,
        DownloadMode? effective = null,
        string? reason = null,
        IReadOnlyList<RunningGame>? sessions = null) => new(
        new AppInfo("0.2.0", "0.21.1", true, BackdropKind.Mica, "", "", 8, 0, 4, 2048, true, null),
        new AuthState(AuthStatus.SignedIn, null),
        Connectivity.Online,
        new LibraryStatus(1, 0, false, null, null, 0),
        new QueueState(jobs ?? [], paused, effective ?? setting, reason),
        sessions ?? [],
        new StorageState([], []),
        new EglState(false, false, false, [], [], true, null),
        new OverlayState(false, false, null, null, false, null, false),
        [],
        Settings.Default with { DownloadMode = setting },
        new OnboardingState(true, OnboardingStep.Done, []));

    private static IReadOnlyList<TrayItem> Items(TrayMenu menu) => menu.Items;

    private static string[] Labels(TrayMenu menu) => [.. menu.Items.Where(i => i.Kind != TrayItemKind.Separator).Select(i => i.Label)];

    // ---------------------------------------------------------------- recent games

    [Fact]
    public void OnlyTheFiveMostRecentlyPlayedInstalledGamesAreListed_NewestFirst()
    {
        var library = Enumerable.Range(0, 8).Select(i => Game("g" + i, "Game " + i, Now.AddHours(-i))).ToList();

        var recent = TrayMenuModel.RecentGames(library);

        Assert.Equal(["g0", "g1", "g2", "g3", "g4"], recent.Select(g => g.AppName).ToArray());
    }

    [Fact]
    public void NeverPlayedHiddenDlcBrokenAndUninstalledGamesAreNotRecent()
    {
        var library = new[]
        {
            Game("never", "Never played"),
            Game("hidden", "Hidden", Now, hidden: true),
            Game("dlc", "DLC", Now, kind: GameKind.Dlc),
            Game("broken", "Broken", Now, InstallStatus.Broken),
            Game("gone", "Gone", Now, InstallStatus.NotInstalled),
            Game("addon", "Launchable add-on", Now.AddMinutes(-1), kind: GameKind.LaunchableAddon),
            Game("ok", "Okay", Now.AddMinutes(-2)),
        };

        Assert.Equal(["addon", "ok"], TrayMenuModel.RecentGames(library).Select(g => g.AppName).ToArray());
    }

    [Fact]
    public void TiesInPlayTimeFallBackToTitleOrder()
    {
        var library = new[] { Game("b", "Beta", Now), Game("a", "alpha", Now) };
        Assert.Equal(["a", "b"], TrayMenuModel.RecentGames(library).Select(g => g.AppName).ToArray());
    }

    [Fact]
    public void RecentGamesPlayWithOneClick()
    {
        var menu = TrayMenuModel.Build(Snapshot(), [Game("a", "Alpha", Now), Game("b", "Beta", Now.AddDays(-1))]);

        var play = menu.Items.Where(i => i.Command == TrayCommand.Launch).ToList();
        Assert.Equal(["Play Alpha", "Play Beta"], play.Select(i => i.Label).ToArray());
        Assert.Equal(["a", "b"], play.Select(i => i.Arg).ToArray());
    }

    [Fact]
    public void ARunningGameOffersCloseInPlaceOfPlay_AndRunningGamesOutsideTheRecentListAreStillShown()
    {
        var sessions = new[]
        {
            new RunningGame("a", "Alpha", RunState.Running, Now, false, false),
            new RunningGame("z", "Untracked old game", RunState.Launching, null, false, false),
            new RunningGame("idle", "Idle", RunState.Idle, null, false, false),
        };

        var menu = TrayMenuModel.Build(Snapshot(sessions: sessions), [Game("a", "Alpha", Now), Game("b", "Beta", Now.AddDays(-1))]);

        var game = menu.Items.Where(i => i.Command is TrayCommand.Launch or TrayCommand.Close).ToList();
        Assert.Equal(["Close Untracked old game", "Close Alpha", "Play Beta"], game.Select(i => i.Label).ToArray());
        Assert.Equal([TrayCommand.Close, TrayCommand.Close, TrayCommand.Launch], game.Select(i => i.Command!.Value).ToArray());
    }

    [Fact]
    public void WithNoGamesPlayedThereIsNoGameSection()
    {
        var menu = TrayMenuModel.Build(Snapshot(), []);

        Assert.Equal(["Open Glider", "Open Downloads", "Download mode: Balanced", "Check for updates", "Quit Glider"], Labels(menu));
        Assert.Equal(TrayItemKind.Separator, menu.Items[1].Kind);
        Assert.DoesNotContain(menu.Items.Zip(menu.Items.Skip(1)), p => p.First.Kind == TrayItemKind.Separator && p.Second.Kind == TrayItemKind.Separator);
        Assert.NotEqual(TrayItemKind.Separator, menu.Items[^1].Kind);
    }

    // ---------------------------------------------------------------- queue

    [Fact]
    public void AnIdleQueueShowsNothingAndOffersNoPauseOrResume()
    {
        var menu = TrayMenuModel.Build(Snapshot([Job("done", "a", "Done", JobStatus.Completed)]), []);

        Assert.DoesNotContain(menu.Items, i => i.Command is TrayCommand.PauseAll or TrayCommand.ResumeAll);
        Assert.DoesNotContain(menu.Items, i => i.Kind == TrayItemKind.Info);
        Assert.Equal("Glider", menu.Tooltip);
    }

    [Fact]
    public void ARunningJobShowsItsPhaseTitleAndPercent_WithPauseAll()
    {
        var jobs = new[]
        {
            Job("1", "fn", "Fortnite", JobStatus.Running, JobKind.Install, JobPhase.Downloading, 42.7),
            Job("2", "b", "Beta", JobStatus.Queued),
            Job("3", "c", "Gamma", JobStatus.Queued),
        };

        var menu = TrayMenuModel.Build(Snapshot(jobs), []);

        var info = menu.Items.Single(i => i.Kind == TrayItemKind.Info);
        Assert.Equal("Downloading Fortnite 42% (+2 more)", info.Label);
        Assert.Contains(menu.Items, i => i.Command == TrayCommand.PauseAll && i.Label == "Pause all downloads");
        Assert.DoesNotContain(menu.Items, i => i.Command == TrayCommand.ResumeAll);
        Assert.Equal("Glider · Downloading Fortnite 42% (+2 more)", menu.Tooltip);
    }

    [Fact]
    public void LiveProgressBeatsTheSnapshotsStalePercent()
    {
        var jobs = new[] { Job("1", "fn", "Fortnite", JobStatus.Running, JobKind.Install, JobPhase.Downloading, 10) };
        var live = new JobProgress("1", "fn", JobPhase.Installing, 88.2, 50, 1, null, null, null, null, null, null, DownloadMode.Balanced, Limiter.Disk);

        var menu = TrayMenuModel.Build(Snapshot(jobs), [], live);
        Assert.Equal("Installing Fortnite 88%", menu.Items.Single(i => i.Kind == TrayItemKind.Info).Label);

        var other = live with { JobId = "other" }; // progress of a different job is ignored
        Assert.Equal("Downloading Fortnite 10%", TrayMenuModel.Build(Snapshot(jobs), [], other).Items.Single(i => i.Kind == TrayItemKind.Info).Label);
    }

    [Theory]
    [InlineData(JobKind.Verify, JobPhase.Verifying, "Verifying")]
    [InlineData(JobKind.Verify, JobPhase.Repairing, "Repairing")]
    [InlineData(JobKind.Move, JobPhase.Moving, "Moving")]
    [InlineData(JobKind.Uninstall, JobPhase.Removing, "Removing")]
    [InlineData(JobKind.Import, JobPhase.Importing, "Importing")]
    [InlineData(JobKind.Update, JobPhase.Preparing, "Updating")]
    [InlineData(JobKind.Install, JobPhase.Finalizing, "Installing")]
    public void VerbFollowsThePhase(JobKind kind, JobPhase phase, string verb)
    {
        var jobs = new[] { Job("1", "a", "Alpha", JobStatus.Running, kind, phase) };
        Assert.Equal($"{verb} Alpha", TrayMenuModel.Build(Snapshot(jobs), []).Items.Single(i => i.Kind == TrayItemKind.Info).Label);
    }

    [Fact]
    public void APausedQueueOffersResumeOnly()
    {
        var jobs = new[] { Job("1", "a", "Alpha", JobStatus.Paused), Job("2", "b", "Beta", JobStatus.Queued) };

        var menu = TrayMenuModel.Build(Snapshot(jobs, paused: true), []);

        Assert.Equal("Downloads paused · 2 in queue", menu.Items.Single(i => i.Kind == TrayItemKind.Info).Label);
        Assert.Contains(menu.Items, i => i.Command == TrayCommand.ResumeAll);
        Assert.DoesNotContain(menu.Items, i => i.Command == TrayCommand.PauseAll);
    }

    [Fact]
    public void AJobPausedOnItsOwnOffersResume_AndAWaitingJobSaysWhy()
    {
        var oneJob = TrayMenuModel.Build(Snapshot([Job("1", "a", "Alpha", JobStatus.Paused)]), []);
        Assert.Equal("Alpha: paused", oneJob.Items.Single(i => i.Kind == TrayItemKind.Info).Label);
        Assert.Contains(oneJob.Items, i => i.Command == TrayCommand.ResumeAll);
        Assert.DoesNotContain(oneJob.Items, i => i.Command == TrayCommand.PauseAll);

        var waiting = TrayMenuModel.Build(Snapshot([Job("1", "a", "Alpha", JobStatus.Waiting, blocked: "Waiting for Fortnite to close")]), []);
        Assert.Equal("Alpha: Waiting for Fortnite to close", waiting.Items.Single(i => i.Kind == TrayItemKind.Info).Label);
        Assert.Contains(waiting.Items, i => i.Command == TrayCommand.PauseAll);
    }

    [Fact]
    public void OpenDownloadsIsAlwaysThere() =>
        Assert.Contains(TrayMenuModel.Build(Snapshot(), []).Items, i => i.Command == TrayCommand.OpenDownloads);

    // ---------------------------------------------------------------- download mode

    [Theory]
    [InlineData(DownloadMode.Background, "background")]
    [InlineData(DownloadMode.Balanced, "balanced")]
    [InlineData(DownloadMode.Full, "full")]
    public void TheModeSubmenuChecksTheSetting(DownloadMode mode, string arg)
    {
        var menu = TrayMenuModel.Build(Snapshot(setting: mode), []);

        var sub = menu.Items.Single(i => i.Kind == TrayItemKind.Submenu);
        Assert.Equal($"Download mode: {TrayMenuModel.ModeName(mode)}", sub.Label);
        Assert.Equal(["Background", "Balanced", "Full speed"], sub.Children!.Select(c => c.Label).ToArray());
        Assert.Equal([TrayCommand.SetMode, TrayCommand.SetMode, TrayCommand.SetMode], sub.Children!.Select(c => c.Command!.Value).ToArray());
        Assert.Equal(arg, sub.Children!.Single(c => c.Checked).Arg);
        Assert.Equal(mode, ParseMode(sub.Children!.Single(c => c.Checked).Arg));
    }

    private static DownloadMode ParseMode(string? arg)
    {
        Assert.True(TrayMenuModel.TryParseMode(arg, out var mode));
        return mode;
    }

    [Fact]
    public void WhenTheEffectiveModeDiffersTheSubmenuSaysWhy()
    {
        var menu = TrayMenuModel.Build(Snapshot(setting: DownloadMode.Full, effective: DownloadMode.Background, reason: "Throttled while Fortnite is running"), []);

        var sub = menu.Items.Single(i => i.Kind == TrayItemKind.Submenu);
        Assert.Equal(DownloadMode.Full, ParseMode(sub.Children!.Single(c => c.Checked).Arg)); // the setting is what is checked
        Assert.Equal("Right now: Background (Throttled while Fortnite is running)", sub.Children!.Single(c => c.Kind == TrayItemKind.Info).Label);
    }

    [Fact]
    public void ModeArgumentsAreStrict()
    {
        Assert.False(TrayMenuModel.TryParseMode(null, out _));
        Assert.False(TrayMenuModel.TryParseMode("Full", out _));
        Assert.False(TrayMenuModel.TryParseMode("turbo", out _));
    }

    // ---------------------------------------------------------------- tooltip

    [Fact]
    public void TheTooltipNamesRunningGamesAndTheQueue_WithinTheWindowsLimit()
    {
        var one = Snapshot(sessions: [new RunningGame("a", "Alpha", RunState.Running, Now, false, false)]);
        Assert.Equal("Glider · Alpha running", TrayMenuModel.Tooltip(one));

        var two = Snapshot(
            [Job("1", "a", "Alpha", JobStatus.Running, JobKind.Update, JobPhase.Downloading, 5)],
            sessions: [new RunningGame("a", "A", RunState.Running, Now, false, false), new RunningGame("b", "B", RunState.Running, Now, false, false)]);
        Assert.Equal("Glider · 2 games running · Downloading Alpha 5%", TrayMenuModel.Tooltip(two));

        var longTitle = Snapshot([Job("1", "a", new string('x', 400), JobStatus.Running, JobKind.Install, JobPhase.Downloading, 5)]);
        var tip = TrayMenuModel.Tooltip(longTitle);
        Assert.Equal(TrayMenuModel.MaxTooltip, tip.Length);
        Assert.EndsWith("…", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildingTheMenuForAFiveHundredGameLibraryIsCheap()
    {
        var library = Enumerable.Range(0, 500).Select(i => Game("g" + i, "Game " + i, i % 3 == 0 ? Now.AddMinutes(-i) : null)).ToList();
        var snapshot = Snapshot([Job("1", "g1", "Game 1", JobStatus.Running, JobKind.Install, JobPhase.Downloading, 1)]);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            _ = TrayMenuModel.Build(snapshot, library);
        }

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"100 builds took {sw.Elapsed.TotalMilliseconds:0} ms");
    }
}
