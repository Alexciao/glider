using Glider.Core.Contracts;
using Glider.Core.Legendary;

namespace Glider.Core.Tests.RealLegendary;

/// <summary>
/// Error classification on the exe's REAL output (not hand-written fixtures): what every online command prints when nobody is signed in,
/// how the auth gate behaves around those logins, and what <c>cleanup</c> really does (the reason Core never runs it).
/// </summary>
[Collection(RealLegendaryCollection.Name)]
public sealed class RealErrorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<GliderError?> ErrorOf(Func<Task<LegendaryOutcome>> run)
    {
        var outcome = await run();
        Assert.False(outcome.Success);
        return outcome.Error;
    }

    private static async Task<GliderError?> ErrorOf(Func<Task> run)
    {
        try
        {
            await run();
            return null;
        }
        catch (GliderException e)
        {
            return e.Error;
        }
    }

    [RealLegendaryFact]
    public async Task EveryOnlineCommandSaysSignInWhenNobodyIsSignedIn()
    {
        // The exe dies with "ValueError: No saved credentials" (a Python traceback) in login(); each of these must become one clean error.
        (string Name, Func<LegendaryCli, Task<GliderError?>> Run)[] commands =
        [
            ("info", cli => ErrorOf(() => cli.GetGameInfoAsync("Fortnite", Ct))),
            ("list", cli => ErrorOf(() => cli.RefreshLibraryAsync(Ct))),
            ("list-installed --check-updates", cli => ErrorOf(() => cli.CheckUpdatesAsync(Ct))),
            ("install", cli => ErrorOf(() => cli.InstallAsync(new InstallArgs("Fortnite", InstallMode.Install, Path.GetTempPath(), 0, 0, null), new Progress<LegendaryProgress>(), Ct))),
            ("activate", cli => ErrorOf(() => cli.ActivateUplayAsync(Ct))),
        ];

        var errors = await Task.WhenAll(commands.Select(async c =>
        {
            using var env = new RealLegendaryEnv();
            using var cli = env.Cli();
            return (c.Name, Error: await c.Run(cli));
        }));

        foreach (var (name, error) in errors)
        {
            Assert.True(error?.Code == ErrorCode.NotSignedIn, $"{name}: {error?.Code} {error?.Detail}");
            Assert.Equal(FixAction.SignIn, error!.Fix);
        }
    }

    [RealLegendaryFact]
    public async Task SignedOutQueriesThatNeedNoLoginAnswerQuietly()
    {
        using var env = new RealLegendaryEnv();
        using var cli = env.Cli();

        Assert.Null(await cli.RefreshSessionAsync(Ct)); // online `status --json` doesn't log in: "<not logged in>" is an answer, not an error
        Assert.Null(await cli.GetUpdateSizesAsync("Fortnite", Ct)); // `update --update-only` dry run: nothing to measure
        Assert.Null(await cli.GetWebExchangeCodeAsync(Ct)); // get-token while signed out: no code, no exception
        await cli.SignOutAsync(Ct); // `auth --delete` with nothing to delete is not an error
    }

    [RealLegendaryFact]
    public async Task ARunThatEndsInATracebackFreesTheAuthGateAtOnce()
    {
        // A failed login never prints a "past login" line, so the gate can only be released by the process exiting (not by the 45 s cap).
        using var env = new RealLegendaryEnv();
        using var cli = env.Cli();

        var started = Environment.TickCount64;
        var first = await ErrorOf(() => cli.GetGameInfoAsync("Fortnite", Ct));
        var second = await ErrorOf(() => cli.CheckUpdatesAsync(Ct));
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);

        Assert.Equal(ErrorCode.NotSignedIn, first?.Code);
        Assert.Equal(ErrorCode.NotSignedIn, second?.Code);
        Assert.False(cli.Runner.Gate.Held);
        Assert.Equal(0, cli.Runner.Gate.Waiting);
        Assert.True(elapsed < AuthGate.DefaultMaxHold, $"two failed logins took {elapsed}");
    }

    [RealLegendaryFact]
    public async Task ConcurrentLoginsQueueAtTheGateAndEveryOneIsAnswered()
    {
        using var env = new RealLegendaryEnv();
        using var cli = env.Cli();
        var waitingSeen = 0;
        using var watch = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!watch.IsCancellationRequested)
            {
                waitingSeen = Math.Max(waitingSeen, cli.Runner.Gate.Waiting);
                await Task.Delay(5);
            }
        }, CancellationToken.None);

        var results = await Task.WhenAll(
            ErrorOf(() => cli.ActivateUplayAsync(Ct)),
            ErrorOf(() => cli.CheckUpdatesAsync(Ct)),
            ErrorOf(() => cli.RefreshLibraryAsync(Ct)),
            ErrorOf(() => cli.GetGameInfoAsync("Fortnite", Ct)));
        await watch.CancelAsync();
        await watcher;

        Assert.All(results, e => Assert.Equal(ErrorCode.NotSignedIn, e?.Code));
        Assert.True(waitingSeen >= 1, "the logins should have queued one behind another");
        Assert.False(cli.Runner.Gate.Held);
        Assert.Equal(0, cli.Runner.Gate.Waiting);
    }

    // ------------------------------------------------------------------ network (an invalid token; opt in with GLIDER_REAL_LEGENDARY_NET=1)

    [RealLegendaryNetworkFact]
    public async Task AnExpiredSessionIsAuthExpiredAndTheGateIsFreeAfterwards()
    {
        using var env = new RealLegendaryEnv();
        env.WriteFakeUser(); // FAKE tokens: Epic's account service rejects the refresh, nothing is signed in
        using var cli = env.Cli(o => o.AllowNetwork = true);

        var e = await ErrorOf(() => cli.RefreshSessionAsync(Ct));

        if (e?.Code == ErrorCode.Network)
        {
            Assert.Skip("Epic's servers are unreachable from here.");
        }

        Assert.Equal(ErrorCode.AuthExpired, e?.Code);
        Assert.Equal(FixAction.SignIn, e!.Fix);
        Assert.False(File.Exists(Path.Combine(env.Config, "user.json"))); // Legendary forgets credentials Epic rejected
        Assert.False(cli.Runner.Gate.Held);
        // …and with the account gone the next login fails fast without touching the network again.
        Assert.Equal(ErrorCode.NotSignedIn, (await ErrorOf(() => cli.CheckUpdatesAsync(Ct)))?.Code);
    }

    [RealLegendaryNetworkFact]
    public async Task OnlineLaunchWithAnExpiredSessionIsAuthExpired()
    {
        using var env = new RealLegendaryEnv();
        VerifyFixture.Install(env);
        env.WriteFakeUser();
        using var cli = env.Cli(o => o.AllowNetwork = true);

        var e = await ErrorOf(() => cli.GetLaunchCommandAsync(VerifyFixture.App, true, "", offline: false, Ct));

        if (e?.Code == ErrorCode.Network)
        {
            Assert.Skip("Epic's servers are unreachable from here.");
        }

        Assert.Equal(ErrorCode.AuthExpired, e?.Code);
    }

    // ------------------------------------------------------------------ cleanup

    [RealLegendaryFact]
    public async Task CleanupDeletesResumeFilesAndManifestsOfEverythingNotInstalled()
    {
        // Why Glider never runs `cleanup`: it wipes the resume state of a paused download and the manifest of a download in flight
        // (docs/research/legendary-v0.2.md §1.4). This pins that behaviour of the exe, the reason for the rule.
        using var env = new RealLegendaryEnv();
        VerifyFixture.Install(env);
        var tmp = Path.Combine(env.Config, "tmp");
        Directory.CreateDirectory(tmp);
        var resume = Path.Combine(tmp, "Paused.resume");
        var sdl = Path.Combine(tmp, "Fortnite.json");
        var installedManifest = Path.Combine(env.Config, "manifests", VerifyFixture.ManifestName);
        var inFlightManifest = Path.Combine(env.Config, "manifests", "Paused_Windows_2.0.manifest");
        File.WriteAllText(resume, "x");
        File.WriteAllText(sdl, "{}");
        File.WriteAllText(inFlightManifest, "x");

        var run = await env.RunAsync("cleanup");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Cleanup complete!", run.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(resume));
        Assert.False(File.Exists(sdl));
        Assert.False(File.Exists(inFlightManifest));
        Assert.True(File.Exists(installedManifest)); // the manifest of an installed game is kept
    }

    [Fact]
    public void CoreNeverBuildsACleanupCommand()
    {
        string? root = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Glider.Core", "Glider.Core.csproj")))
            {
                root = dir.FullName;
                break;
            }
        }

        if (root is null)
        {
            Assert.Skip("The source tree is not next to the test binaries.");
        }

        var hits = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("\"cleanup\"", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(hits);
    }
}
