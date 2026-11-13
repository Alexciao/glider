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
            rig.Service.Hub.Mutate(s => s with { LatestVersion = $"v{i}" });
            rig.Service.Hub.Invalidate();
        }

        Assert.Equal(before, rig.Sink.Of<AppSnapshot>(RpcEvents.State).Count); // nothing until the window closes
        rig.Time.Advance(TimeSpan.FromMilliseconds(40));
        var states = rig.Sink.Of<AppSnapshot>(RpcEvents.State);
        Assert.Equal(before + 1, states.Count);
        Assert.Equal("v499", states[^1].Game.LatestVersion); // trailing edge carries the latest state

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
        var p = new JobProgress("job-1", JobPhase.Downloading, 12.5, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        rig.Jobs.RaiseProgress(p);
        Assert.Equal(p, Assert.Single(rig.Sink.Of<JobProgress>(RpcEvents.Progress)));
    }

    [Fact]
    public async Task SnapshotReflectsStoresAndJobs()
    {
        using var rig = new Rig();
        var rec = rig.Install("v1", sharedWithEgl: true);
        await rig.StartWarmAsync();
        var snap = await rig.Service.GetSnapshot();
        Assert.True(snap.Game.Installed!.SharedWithEgl);
        Assert.True(snap.Egl.SyncAvailable);
        Assert.True(snap.Onboarding.Completed);

        Assert.Equal(InstallStatus.Installed, snap.Game.InstallStatus);
        Assert.Equal(rec.InstallPath, snap.Game.Installed.InstallPath);
        Assert.Contains(snap.Jobs, j => j.Kind == JobKind.Update); // auto-update (latest v2 > v1)
    }

    [Fact]
    public async Task MissingFolderIsBroken()
    {
        using var rig = new Rig();
        rig.Install("v1", path: Path.Combine(rig.Root, "gone"));
        await rig.Service.StartAsync();
        Assert.Equal(InstallStatus.Broken, rig.Sink.Of<AppSnapshot>(RpcEvents.State)[0].Game.InstallStatus);
    }

    [Fact]
    public async Task OnboardingSteps()
    {
        using var rig = new Rig();
        rig.Legendary.Account = null;
        await rig.StartWarmAsync();
        Assert.Equal(new OnboardingState(false, OnboardingStep.Welcome), (await rig.Service.GetSnapshot()).Onboarding);
        await rig.Service.SignIn();
        Assert.Equal(OnboardingStep.ChooseInstall, (await rig.Service.GetSnapshot()).Onboarding.Step);
        await rig.Service.CompleteOnboarding();
        Assert.Equal(new OnboardingState(true, OnboardingStep.Done), (await rig.Service.GetSnapshot()).Onboarding);
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
            nameof(InstallPlanRequest) => new InstallPlanRequest("relative"),
            nameof(InstallRequest) => new InstallRequest(""),
            nameof(ValidateFolderRequest) => new ValidateFolderRequest(""),
            nameof(AdoptRequest) => new AdoptRequest(null, null, (VerifyMode)42),
            nameof(MoveRequest) => new MoveRequest("x"),
            nameof(UninstallRequest) => new UninstallRequest((UninstallMode)9),
            nameof(JobRef) => new JobRef("missing"),
            nameof(Settings) => Settings.Default with { UpdateCheckIntervalMinutes = 0 },
            _ => null,
        };
        yield return [garbage];
    }

    [Fact]
    public async Task UnexpectedExceptionsBecomeUnknownOrNetwork()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        rig.Time.Advance(TimeSpan.FromMinutes(11));
        rig.Legendary.InfoError = new InvalidOperationException("boom");
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.CheckForUpdates());
        Assert.Equal(ErrorCode.Network, e.Error.Code);

        e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.PauseJob(new JobRef("nope")));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
    }

    [Fact]
    public async Task ThroughTheDispatcher_ErrorsAreSerialized()
    {
        using var rig = new Rig();
        var transport = new Transport();
        var d = new RpcDispatcher(rig.Service, transport);
        await d.HandleAsync("""{"type":"request","id":1,"method":"game.launch","params":null}""");
        Assert.Contains("\"code\":\"notInstalled\"", transport.Last, StringComparison.Ordinal);
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
        await rig.Service.Close();
        Assert.True(rig.Calls.Has("host.close:True"));
        await rig.Service.Ready();
        Assert.True(rig.Calls.Has("host.reveal"));
        await rig.Service.OpenInstallFolder();
        Assert.Contains(game, rig.Host.Opened);
        await rig.Service.OpenExternal(new UrlRequest("https://example.com/x"));
        Assert.Contains("https://example.com/x", rig.Host.Opened);
    }
}
