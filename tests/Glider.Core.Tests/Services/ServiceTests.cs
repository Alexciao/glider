using System.Reflection;
using Glider.Core.Contracts;

namespace Glider.Core.Tests.Services;

public class StateHubTests
{
    [Fact]
    public async Task ManyChangesCoalesceIntoFewStateEvents()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var before = rig.Sink.Of<AppSnapshot>(RpcEvents.State).Count;

        for (var i = 0; i < 500; i++)
        {
            var v = $"0.21.{i}";
            rig.Service.Hub.Mutate(s => s with { LegendaryVersion = v });
            rig.Service.Hub.Invalidate();
        }

        Assert.Equal(before, rig.Sink.Of<AppSnapshot>(RpcEvents.State).Count); // nothing until the window closes
        rig.Time.Advance(TimeSpan.FromMilliseconds(40));
        var states = rig.Sink.Of<AppSnapshot>(RpcEvents.State);
        Assert.Equal(before + 1, states.Count);
        Assert.Equal("0.21.499", states[^1].App.LegendaryVersion); // trailing edge carries the latest state

        // 2 s of continuous changes at 1 kHz → ≤ ~30/s.
        for (var ms = 0; ms < 2000; ms++)
        {
            rig.Service.Hub.Invalidate();
            rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        }

        var published = rig.Sink.Of<AppSnapshot>(RpcEvents.State).Count - (before + 1);
        Assert.InRange(published, 50, 62);
    }

    [Fact]
    public async Task ProgressIsForwardedImmediately()
    {
        using var rig = new Rig();
        await rig.Service.StartAsync();
        var p = new JobProgress("job-1", "Fortnite", JobPhase.Downloading, 12.5, 12.5, 3, null, null, null, null, null, null, DownloadMode.Balanced, Limiter.Unknown);
        rig.Jobs.RaiseProgress(p);
        Assert.Equal(p, Assert.Single(rig.Sink.Of<JobProgress>(RpcEvents.Progress)));
    }

    [Fact]
    public async Task SnapshotIsSmall_AndTheLibraryCarriesTheGames()
    {
        using var rig = new Rig();
        var rec = rig.Install("v1", sharedWithEgl: true);
        for (var i = 0; i < 300; i++)
        {
            rig.AddGame($"game{i:000}", $"Game {i}");
        }

        await rig.StartWarmAsync();
        var snap = await rig.Service.GetSnapshot();
        Assert.Equal(301, snap.Library.GameCount);
        Assert.Equal(1, snap.Library.UpdatesAvailable);
        Assert.Contains(snap.Queue.Jobs, j => j.Kind == JobKind.Update && j.AppName == FortniteLayout.AppName); // auto-update (latest v2 > v1)
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snap, GliderJsonContext.Default.AppSnapshot);
        Assert.True(bytes.Length < 16 * 1024, $"snapshot is {bytes.Length} bytes");

        var fortnite = (await rig.SummaryAsync())!;
        Assert.True(fortnite.SharedWithEgl);
        Assert.Equal(InstallStatus.Installed, fortnite.InstallStatus);
        Assert.Equal(rec.InstallPath, fortnite.InstallPath);
        Assert.True(fortnite.UpdateAvailable);
    }

    [Fact]
    public async Task MissingFolderIsBroken()
    {
        using var rig = new Rig();
        rig.Install("v1", path: Path.Combine(rig.Root, "gone"));
        await rig.StartWarmAsync();
        Assert.Equal(InstallStatus.Broken, (await rig.SummaryAsync())!.InstallStatus);
    }
}

public class OnboardingTests
{
    [Fact]
    public async Task StepsPersist_SkippedStepsAreRemembered_AndItCanBeRestarted()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        Assert.Equal(OnboardingStep.Welcome, (await rig.Service.GetSnapshot()).Onboarding.Step);

        await rig.Service.SetOnboardingStep(new OnboardingStepRequest(OnboardingStep.SignIn, false));
        await rig.Service.SetOnboardingStep(new OnboardingStepRequest(OnboardingStep.Scan, true)); // sign-in skipped
        var s = await rig.Service.SetOnboardingStep(new OnboardingStepRequest(OnboardingStep.Folders, false));
        Assert.Equal(OnboardingStep.Folders, s.Step);
        Assert.Equal([OnboardingStep.SignIn], s.Skipped);
        Assert.False(s.Completed);
        Assert.Equal(s, rig.Onboarding.Current); // persisted (resumes after a restart)

        var done = await rig.Service.CompleteOnboarding();
        Assert.True(done.Completed);
        Assert.Equal(OnboardingStep.Done, done.Step);
        Assert.Equal(done, (await rig.Service.GetSnapshot()).Onboarding);

        var again = await rig.Service.RestartOnboarding();
        Assert.Equal(new OnboardingState(false, OnboardingStep.Welcome, []), again with { Skipped = [] });
        Assert.Empty(again.Skipped);
    }
}

public class LibraryRpcTests
{
    [Fact]
    public async Task FavoritesHiddenAndPerGameSettings_ArePersistedAndPublishedAsDeltas()
    {
        using var rig = new Rig();
        rig.Install("v2");
        await rig.StartWarmAsync();
        var start = (await rig.Service.GetLibrary()).Revision;

        await rig.Service.SetFavorite(new GameFlagRequest(FortniteLayout.AppName, true));
        await rig.Service.SetHidden(new GameFlagRequest(FortniteLayout.AppName, true));
        await rig.Service.LibraryForTests.FlushAsync();
        var g = rig.Service.LibraryForTests.Summary(FortniteLayout.AppName)!;
        Assert.True(g.Favorite);
        Assert.True(g.Hidden);
        Assert.True(rig.Games.Get(FortniteLayout.AppName).Favorite);
        var deltas = rig.Sink.Of<LibraryDelta>(RpcEvents.Library).Where(d => d.Revision > start).ToList();
        Assert.NotEmpty(deltas);
        Assert.All(deltas, d => Assert.Equal([FortniteLayout.AppName], d.Upserts.Select(u => u.AppName)));

        var settings = await rig.Service.UpdateGameSettings(new GameSettingsRequest(FortniteLayout.AppName,
            GameSettings.Default with { LaunchArgs = "  -windowed  ", AutoUpdate = AutoUpdatePolicy.Never, LaunchMode = LaunchMode.Offline }));
        Assert.Equal("-windowed", settings.LaunchArgs);
        Assert.Equal(AutoUpdatePolicy.Never, rig.Games.Get(FortniteLayout.AppName).Settings.AutoUpdate);
        Assert.Equal("-windowed", (await rig.Service.GetGameDetail(new AppRef(FortniteLayout.AppName))).Settings.LaunchArgs);

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.UpdateGameSettings(new GameSettingsRequest(FortniteLayout.AppName,
            GameSettings.Default with { ExecutableOverride = @"..\..\evil.exe" })));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
    }

    [Fact]
    public async Task Collections_CreateRenameMembershipDelete()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        var c = await rig.Service.CreateCollection(new CollectionCreateRequest("  Shooters "));
        Assert.Equal("Shooters", c.Name);
        c = await rig.Service.SetCollectionMembership(new CollectionMembershipRequest(c.Id, FortniteLayout.AppName, true));
        Assert.Equal([FortniteLayout.AppName], c.AppNames);
        c = await rig.Service.RenameCollection(new CollectionRenameRequest(c.Id, "Battle royale"));
        Assert.Equal("Battle royale", Assert.Single((await rig.Service.GetSnapshot()).Collections).Name);
        c = await rig.Service.SetCollectionMembership(new CollectionMembershipRequest(c.Id, FortniteLayout.AppName, false));
        Assert.Empty(c.AppNames);
        await rig.Service.DeleteCollection(new CollectionRef(c.Id));
        Assert.Empty((await rig.Service.GetSnapshot()).Collections);

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.CreateCollection(new CollectionCreateRequest(" ")));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
        e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.RenameCollection(new CollectionRenameRequest("missing", "x")));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
    }

    [Fact]
    public async Task UpdateAll_QueuesEveryPendingUpdateExceptNever()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Ask });
        rig.Install("v1");
        rig.AddGame("A", "Game A", latest: "2.0", installedVersion: "1.0");
        rig.AddGame("B", "Game B", latest: "2.0", installedVersion: "1.0");
        rig.AddGame("C", "Game C", latest: "1.0", installedVersion: "1.0");
        rig.Games.Put("B", rig.Games.Get("B") with { Settings = GameSettings.Default with { AutoUpdate = AutoUpdatePolicy.Never } });
        await rig.StartWarmAsync();

        var result = await rig.Service.UpdateAll();
        Assert.Equal(new[] { "A", FortniteLayout.AppName }.Order(), result.Jobs.Select(j => j.AppName).Order());
        Assert.All(result.Jobs, j => Assert.Equal(JobKind.Update, j.Kind));
    }

    [Fact]
    public async Task Phase2Methods_AnswerWithEmptyStatesOrNotAvailableYet()
    {
        using var rig = new Rig();
        rig.Install("v2");
        await rig.StartWarmAsync();
        var saves = await rig.Service.GetSaves(new AppRef(FortniteLayout.AppName));
        Assert.Equal(SaveSyncStatus.NotSupported, saves.Status); // Fortnite declares no CloudSaveFolder
        Assert.False((await rig.Service.GetSnapshot()).Overlay.Installed);
        Assert.False((await rig.Service.ShowStore(new StoreShowRequest(null, new StoreBounds(0, 0, 10, 10)))).Created);
        Assert.Empty((await rig.Service.GetFreeGames()).Items);

        foreach (var call in new Func<Task>[]
                 {
                     () => rig.Service.SetShortcut(new ShortcutRequest(FortniteLayout.AppName, ShortcutLocation.Desktop, true)),
                 })
        {
            var e = await Assert.ThrowsAsync<GliderException>(call);
            Assert.Equal(ErrorCode.Unknown, e.Error.Code);
            Assert.Equal("Not available yet", e.Error.Message);
        }
    }
}

public class RpcErrorTests
{
    /// <summary>Every RPC method, called with null/garbage in an empty install, fails only with GliderException.</summary>
    [Fact]
    public async Task EveryMethodReturnsStructuredErrors()
    {
        using var rig = new Rig();
        rig.Legendary.Account = null;
        rig.Host.AuthCode = null;
        await rig.StartWarmAsync();

        foreach (var m in typeof(IGliderRpc).GetMethods())
        {
            if (m.Name is nameof(IGliderRpc.Quit))
            {
                continue;
            }

            foreach (var arg in ArgsFor(m))
            {
                try
                {
                    var task = (Task)m.Invoke(rig.Service, arg)!;
                    await task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (GliderException)
                {
                }
                catch (Exception e)
                {
                    Assert.Fail($"{m.Name}({arg.FirstOrDefault()?.ToString() ?? "null"}) threw {e.GetType().Name}: {e.Message}");
                }
            }
        }
    }

    private static IEnumerable<object?[]> ArgsFor(MethodInfo m)
    {
        var ps = m.GetParameters();
        if (ps.Length == 0)
        {
            yield return [];
            yield break;
        }

        yield return [null];
        var t = ps[0].ParameterType;
        object? garbage = t.Name switch
        {
            nameof(UrlRequest) => new UrlRequest("javascript:alert(1)"),
            nameof(PathRequest) => new PathRequest("C:\\Windows\\notepad.exe"),
            nameof(PickFolderRequest) => new PickFolderRequest("rel", null),
            nameof(LogsRequest) => new LogsRequest(-5),
            nameof(AppRef) => new AppRef("no-such-game"),
            nameof(GameFlagRequest) => new GameFlagRequest("", true),
            nameof(InstallPlanRequest) => new InstallPlanRequest("Fortnite", "relative", null, null),
            nameof(InstallRequest) => new InstallRequest("Fortnite", "", null, null, false),
            nameof(ValidateFolderRequest) => new ValidateFolderRequest(""),
            nameof(LocateRequest) => new LocateRequest("", (VerifyMode)42),
            nameof(EglImportRequest) => new EglImportRequest([], (VerifyMode)42),
            nameof(MoveRequest) => new MoveRequest("Fortnite", "x"),
            nameof(UninstallRequest) => new UninstallRequest("Fortnite", (UninstallMode)9),
            nameof(LaunchRequest) => new LaunchRequest("Fortnite", (LaunchMode)7, false),
            nameof(GameSettingsRequest) => new GameSettingsRequest("Fortnite", GameSettings.Default with { LaunchArgs = "a\nb" }),
            nameof(JobRef) => new JobRef("missing"),
            nameof(JobReorderRequest) => new JobReorderRequest("missing", -1),
            nameof(CollectionCreateRequest) => new CollectionCreateRequest(""),
            nameof(OnboardingStepRequest) => new OnboardingStepRequest((OnboardingStep)99, false),
            nameof(Settings) => Settings.Default with { UpdateCheckIntervalMinutes = 0 },
            _ => null,
        };
        yield return [garbage];
    }

    [Fact]
    public async Task UnknownJobsAndGamesAreInvalidOrNotFound()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.PauseJob(new JobRef("nope")));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
        e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.GetGameDetail(new AppRef("nope")));
        Assert.Equal(ErrorCode.NotFound, e.Error.Code);
    }

    [Fact]
    public async Task ThroughTheDispatcher_ErrorsAreSerialized()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        var transport = new Transport();
        var d = new RpcDispatcher(rig.Service, transport);
        await d.HandleAsync("""{"type":"request","id":1,"method":"game.launch","params":{"appName":"Fortnite","mode":null,"skipSaveSync":false}}""");
        Assert.Contains("\"code\":\"notInstalled\"", transport.Last, StringComparison.Ordinal);
        Assert.Contains("\"appName\":\"Fortnite\"", transport.Last, StringComparison.Ordinal);
    }

    private sealed class Transport : IRpcTransport
    {
        public string Last { get; private set; } = "";
        public void Send(string json) => Last = json;
    }
}

public class AuthAndSettingsTests
{
    [Fact]
    public async Task SignInCancelled_RestoresStateAndThrows()
    {
        using var rig = new Rig();
        rig.Legendary.Account = null;
        rig.Host.AuthCode = null;
        await rig.StartWarmAsync();
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.SignIn());
        Assert.Equal(ErrorCode.AuthCancelled, e.Error.Code);
        Assert.Equal(AuthStatus.SignedOut, (await rig.Service.GetSnapshot()).Auth.Status);
        Assert.DoesNotContain(rig.Calls.All, c => c.StartsWith("legendary.signIn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignInAndOut()
    {
        using var rig = new Rig();
        rig.Legendary.Account = null;
        await rig.StartWarmAsync();
        var state = await rig.Service.SignIn();
        Assert.Equal(AuthStatus.SignedIn, state.Status);
        Assert.Equal("Alice", state.Account!.DisplayName);
        Assert.Null(state.Account.AvatarUrl);
        Assert.True(rig.Calls.Has("legendary.signInToken:exchange-code"));
        Assert.Equal(Connectivity.Online, (await rig.Service.GetSnapshot()).Connectivity);

        state = await rig.Service.SignOut();
        Assert.Equal(AuthStatus.SignedOut, state.Status);
        Assert.True(rig.Calls.Has("legendary.signOut"));
        Assert.True(rig.Calls.Has("host.clearLogin"));
    }

    [Fact]
    public async Task UpdateSettings_AppliesHostAndLogLevel_AndValidates()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        var saved = await rig.Service.UpdateSettings(Settings.Default with { StartWithWindows = true, LogLevel = LogLevel.Debug, DefaultInstallBase = "" });
        Assert.Equal([true], rig.Host.StartWithWindows);
        Assert.Equal(LogLevel.Debug, rig.Log.MinimumLevel);
        Assert.Null(saved.DefaultInstallBase);

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.UpdateSettings(Settings.Default with { DownloadWorkers = 0 }));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
        e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.UpdateSettings(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Default }));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);

        var before = rig.Jobs.ReevaluateCount;
        await rig.Service.UpdateSettings(Settings.Default with { WhilePlaying = WhilePlayingPolicy.Throttle });
        Assert.True(rig.Jobs.ReevaluateCount > before); // the writer lane re-reads the policy

        await rig.Service.ResetSettings();
        Assert.Equal([true, false], rig.Host.StartWithWindows);
    }

    [Fact]
    public async Task LogsCarryDiagnosticsHeader_WithoutSecrets()
    {
        using var rig = new Rig();
        rig.Install("v1");
        await rig.StartWarmAsync();
        rig.Log.Info("x", "cmd -AUTH_PASSWORD=abc123 \"access_token\": \"tok\"");
        var logs = await rig.Service.GetLogs(new LogsRequest(100));
        Assert.Contains("Glider diagnostics", logs.Text, StringComparison.Ordinal);
        Assert.Contains("Legendary:  0.21.1", logs.Text, StringComparison.Ordinal);
        Assert.Contains("Install:    Fortnite:", logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tok\"", logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("acc-1", logs.Text, StringComparison.Ordinal);

        await rig.Service.CopyLogs();
        Assert.Contains("Glider diagnostics", rig.Host.Clipboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LicensesIncludeLegendarySourceLink()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.Combine(rig.Root, "licenses"));
        File.WriteAllText(Path.Combine(rig.Root, "licenses", "legendary.txt"), "GNU GPL v3");
        var l = (await rig.Service.GetLicenses()).Items;
        var leg = l.Single(i => i.Name == "Legendary");
        Assert.Equal("https://github.com/legendary-gl/legendary/tree/0.21.1", leg.Url);
        Assert.StartsWith("GPL-3.0", leg.License, StringComparison.Ordinal);
        Assert.Equal("GNU GPL v3", leg.Text);
    }

    [Fact]
    public async Task WindowAndShellMethodsGoToHost()
    {
        using var rig = new Rig();
        var game = rig.MakeGameFolder();
        rig.Install("v1", path: game);
        await rig.StartWarmAsync();
        await rig.Service.Close();
        Assert.True(rig.Calls.Has("host.close:True"));
        await rig.Service.Ready();
        Assert.True(rig.Calls.Has("host.reveal"));
        await rig.Service.OpenInstallFolder(new AppRef(FortniteLayout.AppName));
        Assert.Contains(game, rig.Host.Opened);
        await rig.Service.OpenExternal(new UrlRequest("https://example.com/x"));
        Assert.Contains("https://example.com/x", rig.Host.Opened);
    }
}
