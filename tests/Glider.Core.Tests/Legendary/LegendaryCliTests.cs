using System.Text.Json.Nodes;
using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

public class LegendaryCliTests
{
    private const string Build = "++Fortnite+Release-33.00-CL-38324456-Windows";

    private static LegendaryCli Cli(FakeLegendaryEnv env, TimeSpan? stall = null) => new(env.Paths, env.Log, new LegendaryOptions
    {
        StallTimeout = stall ?? TimeSpan.FromSeconds(30),
        SharedMemoryRetryCeilingMb = 8192,
        // These tests exercise the CLI paths; the no-spawn shortcuts have their own tests below.
        PinnedVersion = null,
        ReadAccountFromUserJson = false,
    });

    private static readonly IProgress<LegendaryProgress> NoProgress = new Progress<LegendaryProgress>();

    [Fact]
    public async Task InstallUsesTheUnattendedRecipeAndSucceedsOnTheMarker()
    {
        using var env = new FakeLegendaryEnv();
        var basePath = Path.Combine(env.Root, "Games");
        env.Script("install", $"""
            download chunks=300 files=3 mib=30 interval=5
            installed version={Build} path="{"{basepath}"}/Fortnite"
            err [cli] INFO: Finished installation process in 0.10 seconds.
            """);
        using var cli = Cli(env);
        var outcome = await cli.InstallAsync(new InstallArgs("Fortnite", InstallMode.Install, basePath, 12, 3072, "fastly"), NoProgress, TestContext.Current.CancellationToken);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        Assert.Equal((long)(30 * 1024 * 1024), outcome.DownloadSizeBytes);
        var (cmd, args) = env.Invocations().Single();
        Assert.Equal("install", cmd);
        Assert.Equal(["-y", "install", "Fortnite", "--base-path", basePath, "--skip-sdl", "--skip-dlcs", "--max-workers", "12", "--max-shared-memory", "3072", "--preferred-cdn", "fastly"], args);
        Assert.Equal(Build, (await cli.GetInstalledAsync("Fortnite", TestContext.Current.CancellationToken))!.Version);
    }

    [Fact]
    public async Task ExitZeroWithoutRegistrationIsAFailure()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("install", "err [cli] INFO: Finished installation process in 0.10 seconds.");
        using var cli = Cli(env);
        var outcome = await cli.InstallAsync(new InstallArgs("Fortnite", InstallMode.Install, env.Root, 0, 0, null), NoProgress, TestContext.Current.CancellationToken);
        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.LegendaryFailed, outcome.Error?.Code);
    }

    [Fact]
    public async Task RetriesOnceWithLegendarysSuggestedCacheSize()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("synthetic-install-memoryerror.stderr.txt");
        env.Script("install", """
            errfile synthetic-install-memoryerror.stderr.txt
            exit 1
            """, invocation: 1);
        env.Script("install", """
            installed version=1 path={basepath}/Fortnite
            err [cli] INFO: Finished installation process in 0.10 seconds.
            """, invocation: 2);
        using var cli = Cli(env);
        var outcome = await cli.InstallAsync(new InstallArgs("Fortnite", InstallMode.Install, env.Root, 8, 2048, null), NoProgress, TestContext.Current.CancellationToken);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        var calls = env.Invocations();
        Assert.Equal(2, calls.Count);
        Assert.Equal("2048", calls[0].Args[Array.IndexOf(calls[0].Args, "--max-shared-memory") + 1]);
        Assert.Equal("2560", calls[1].Args[Array.IndexOf(calls[1].Args, "--max-shared-memory") + 1]); // ceil((2182+128)/256)*256
    }

    [Fact]
    public async Task SdlFailureAbortsBeforeAnythingIsDownloadedOrDeleted()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("synthetic-install-sdl-failed.stderr.txt");
        env.Script("install", """
            errfile synthetic-install-sdl-failed.stderr.txt
            hang
            """);
        using var cli = Cli(env);
        var outcome = await cli.InstallAsync(new InstallArgs("Fortnite", InstallMode.Install, env.Root, 0, 0, null), NoProgress, TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCode.Network, outcome.Error?.Code);
        Assert.Contains("SDL", outcome.Error!.Message + outcome.Error.Detail);
    }

    [Fact]
    public async Task UpdateOfAMissingInstallFailsWithoutSpawning()
    {
        using var env = new FakeLegendaryEnv();
        using var cli = Cli(env);
        var outcome = await cli.InstallAsync(new InstallArgs("Fortnite", InstallMode.Update, null, 0, 0, null), NoProgress, TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCode.NotInstalled, outcome.Error?.Code);
        Assert.Empty(env.Invocations());
    }

    [Fact]
    public async Task StatusMapsSignedOutAndSignedIn()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("status-offline-json.stdout.txt");
        env.UseFixture("synthetic-status-signedin.stdout.txt");
        env.Script("status", "outfile status-offline-json.stdout.txt", invocation: 1);
        env.Script("status", "outfile synthetic-status-signedin.stdout.txt", invocation: 2);
        File.Copy(Fixture.PathOf("synthetic-user.json"), Path.Combine(env.Config, "user.json"));
        using var cli = Cli(env);
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await cli.GetAccountOfflineAsync(ct));
        var account = await cli.GetAccountOfflineAsync(ct);
        Assert.Equal(new LegendaryAccount("Åsa 日本", "0123456789abcdef0123456789abcdef"), account);
        Assert.Equal(["status", "--offline", "--json"], env.Invocations()[0].Args);
    }

    [Fact]
    public async Task OfflineAccountIsReadFromUserJsonWithoutSpawning()
    {
        using var env = new FakeLegendaryEnv();
        using var cli = new LegendaryCli(env.Paths, env.Log);
        var ct = TestContext.Current.CancellationToken;
        var userJson = Path.Combine(env.Config, "user.json");

        Assert.Null(await cli.GetAccountOfflineAsync(ct)); // no user.json = signed out
        File.Copy(Fixture.PathOf("synthetic-user.json"), userJson);
        Assert.Equal(new LegendaryAccount("Åsa 日本", "0123456789abcdef0123456789abcdef"), await cli.GetAccountOfflineAsync(ct));
        Assert.Equal("0.21.1", await cli.GetVersionAsync(ct)); // pinned constant from build/legendary.json
        Assert.Empty(env.Invocations());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"access_token\": \"eg1~")]
    [InlineData("{}")]
    [InlineData("[1]")]
    public async Task UnclearUserJsonFallsBackToLegendary(string content)
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("status-offline-json.stdout.txt");
        env.Script("status", "outfile status-offline-json.stdout.txt");
        File.WriteAllText(Path.Combine(env.Config, "user.json"), content);
        using var cli = new LegendaryCli(env.Paths, env.Log);

        Assert.Null(await cli.GetAccountOfflineAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["status", "--offline", "--json"], env.Invocations().Single().Args);
    }

    [Fact]
    public async Task PinnedVersionStillReportsAMissingExe()
    {
        using var dir = new TempDir();
        using var cli = new LegendaryCli(new AppPaths(dir.Path, Path.Combine(dir.Path, "missing.exe")), new TestLog());
        var e = await Assert.ThrowsAsync<GliderException>(() => cli.GetVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.LegendaryMissing, e.Error.Code);
    }

    [Fact]
    public async Task RefreshSurvivesLegendaryApiOutageButReportsExpiry()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("status-network-failure.stderr.txt");
        env.UseFixture("synthetic-status-expired.stderr.txt");
        env.Script("status", """
            err [Core] INFO: Trying to re-use existing login session...
            errfile status-network-failure.stderr.txt
            exit 1
            """, invocation: 1);
        env.Script("status", """
            errfile synthetic-status-expired.stderr.txt
            exit 1
            """, invocation: 2);
        File.Copy(Fixture.PathOf("synthetic-user.json"), Path.Combine(env.Config, "user.json"));
        using var cli = Cli(env);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("Åsa 日本", (await cli.RefreshSessionAsync(ct))?.DisplayName);
        var e = await Assert.ThrowsAsync<GliderException>(() => cli.RefreshSessionAsync(ct));
        Assert.Equal(ErrorCode.AuthExpired, e.Error.Code);
        Assert.Equal(["status", "--json"], env.Invocations()[0].Args);
    }

    [Fact]
    public async Task SignInReplacesAStillValidSession()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("synthetic-auth-still-valid.stderr.txt");
        env.UseFixture("auth-delete.stderr.txt");
        env.UseFixture("synthetic-auth-success.stderr.txt");
        env.Script("auth", "errfile synthetic-auth-still-valid.stderr.txt", invocation: 1);
        env.Script("auth", """
            deletefile user.json
            errfile auth-delete.stderr.txt
            """, invocation: 2);
        // the successful sign-in writes user.json the way Legendary does
        env.Script("auth", """
            writefile user.json {"displayName": "Åsa 日本", "account_id": "0123456789abcdef0123456789abcdef"}
            errfile synthetic-auth-success.stderr.txt
            """, invocation: 3);
        File.Copy(Fixture.PathOf("synthetic-user.json"), Path.Combine(env.Config, "user.json"));
        using var cli = Cli(env);

        var account = await cli.SignInWithExchangeTokenAsync(" \"abc123\" ", TestContext.Current.CancellationToken);

        Assert.Equal(new LegendaryAccount("Åsa 日本", "0123456789abcdef0123456789abcdef"), account);
        var calls = env.Invocations();
        Assert.Equal(["auth", "--token", "abc123", "--disable-webview"], calls[0].Args);
        Assert.Equal(["auth", "--delete"], calls[1].Args);
        Assert.Equal(["auth", "--token", "abc123", "--disable-webview"], calls[2].Args);
        Assert.False(env.Log.Contains("abc123"));
    }

    [Fact]
    public async Task FailedSignInThrowsAuthFailed()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("synthetic-auth-failed.stderr.txt");
        env.Script("auth", "errfile synthetic-auth-failed.stderr.txt");
        using var cli = Cli(env);
        var e = await Assert.ThrowsAsync<GliderException>(() => cli.SignInWithExchangeTokenAsync("bad", TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.AuthFailed, e.Error.Code);
    }

    [Fact]
    public async Task GameInfoCombinesInfoJsonMetadataAndSdlTags()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("synthetic-info-fortnite.stdout.txt");
        env.Script("info", "outfile synthetic-info-fortnite.stdout.txt");
        File.Copy(Fixture.PathOf("synthetic-metadata-Fortnite.json"), Path.Combine(env.Config, "metadata", "Fortnite.json"));
        var sdl = new JsonObject { ["version"] = 14, ["data"] = JsonNode.Parse(File.ReadAllText(Fixture.PathOf("api-sdl-Fortnite.json"))) };
        await File.WriteAllTextAsync(Path.Combine(env.Config, "tmp", "Fortnite.json"), sdl.ToJsonString(), TestContext.Current.CancellationToken);
        using var cli = Cli(env);

        var info = await cli.GetGameInfoAsync("Fortnite", TestContext.Current.CancellationToken);

        Assert.Equal(Build, info.Version);
        Assert.Equal("Fortnite", info.FolderName);
        Assert.Equal("FortniteGame/Binaries/Win64/FortniteLauncher.exe", info.LaunchExecutable);
        Assert.Equal("4fe75bbc5a674f4f9b356b5c90567da5", info.CatalogItemId);
        Assert.Equal("fn", info.Namespace);
        Assert.Equal(["DieselGameBox", "DieselGameBoxTall", "DieselGameBoxLogo"], info.KeyImages.Select(k => k.Type));
        Assert.Equal(2560, info.KeyImages[0].Width);
        Assert.Equal("EAC", info.Prerequisites?.Name);
        Assert.Equal(["9d8ea3b85e7c4bde8bb0c36d79ab3a4a"], info.Prerequisites!.Ids);
        Assert.Equal(43_000_000_000, info.InstallSizeBytes); // __required tags only, StartupOptional excluded
        Assert.Equal(["info", "Fortnite", "--json"], env.Invocations()[0].Args);
    }

    [Fact]
    public async Task GameInfoWhenSignedOutIsNotSignedIn()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("info-fortnite-notloggedin.stdout.txt");
        env.Script("info", "outfile info-fortnite-notloggedin.stdout.txt");
        using var cli = Cli(env);
        var e = await Assert.ThrowsAsync<GliderException>(() => cli.GetGameInfoAsync("Fortnite", TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.NotSignedIn, e.Error.Code);
    }

    [Fact]
    public async Task ReadsInstalledJsonAndManifestPathDirectly()
    {
        using var env = new FakeLegendaryEnv();
        File.Copy(Fixture.PathOf("synthetic-installed.json"), Path.Combine(env.Config, "installed.json"));
        var manifest = Path.Combine(env.Config, "manifests", $"Fortnite_Windows_{Build}.manifest");
        await File.WriteAllBytesAsync(manifest, [1, 2, 3], TestContext.Current.CancellationToken);
        using var cli = Cli(env);

        var installed = await cli.GetInstalledAsync("Fortnite", TestContext.Current.CancellationToken);
        Assert.NotNull(installed);
        Assert.Equal(@"D:\Games\Fortnite", installed.InstallPath);
        Assert.Equal(51_843_123_456, installed.InstallSizeBytes);
        Assert.Equal(["", "Startup", "FrontEnd", "FortniteBR"], installed.InstallTags);
        Assert.Null(installed.EglGuid);
        Assert.False(installed.NeedsVerification);
        Assert.Equal(manifest, cli.GetInstalledManifestPath("Fortnite", Build));
        Assert.Null(cli.GetInstalledManifestPath("Fortnite", "other"));
        Assert.Null(await cli.GetInstalledAsync("NotAGame", TestContext.Current.CancellationToken));
        Assert.Empty(env.Invocations());
    }

    [Fact]
    public async Task LaunchCommandAndOutOfDate()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("synthetic-launch-fortnite.stdout.txt");
        env.UseFixture("synthetic-launch-outofdate.stderr.txt");
        env.Script("launch", "outfile synthetic-launch-fortnite.stdout.txt", invocation: 1);
        env.Script("launch", """
            errfile synthetic-launch-outofdate.stderr.txt
            exit 1
            """, invocation: 2);
        using var cli = Cli(env);
        var ct = TestContext.Current.CancellationToken;

        var cmd = await cli.GetLaunchCommandAsync("Fortnite", skipVersionCheck: true, "-nosplash", false, ct);
        Assert.EndsWith("FortniteLauncher.exe", cmd.Executable, StringComparison.Ordinal);
        Assert.Equal("-nosplash", cmd.Arguments[0]);
        Assert.Contains("-AUTH_PASSWORD=0123456789abcdef0123456789abcdef", cmd.Arguments);
        Assert.Equal(["launch", "Fortnite", "--json", "--skip-version-check"], env.Invocations()[0].Args);
        Assert.False(env.Log.Contains("0123456789abcdef0123456789abcdef")); // exchange code never logged

        var e = await Assert.ThrowsAsync<GliderException>(() => cli.GetLaunchCommandAsync("Fortnite", false, "", false, ct));
        Assert.Equal(ErrorCode.LaunchFailed, e.Error.Code);
    }

    [Fact]
    public async Task VerifyReportsProgressAndCorruption()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("verify", "verify files=40 interval=1 corrupt=1 missing=1");
        using var cli = Cli(env);
        var sink = new LegendaryProcessRunnerTests.RecordingSink();
        var outcome = await cli.VerifyAsync("Fortnite", sink, TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCode.Corrupted, outcome.Error?.Code);
        Assert.Equal(FixAction.Repair, outcome.Error?.Fix);
        Assert.Equal(100, sink.Progress[^1].Percent);
        Assert.Contains(JobPhase.Verifying, sink.Phases);
        Assert.Equal(["verify", "Fortnite"], env.Invocations()[0].Args);
    }

    [Fact]
    public async Task ImportUninstallMoveAndVersion()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("synthetic-import-egl.stderr.txt");
        env.UseFixture("synthetic-uninstall-ok.stderr.txt");
        env.UseFixture("synthetic-move-ok.stderr.txt");
        env.UseFixture("version.txt");
        var game = Path.Combine(env.Root, "Games", "Fortnite");
        Directory.CreateDirectory(game);
        env.Script("import", """
            installed version=1 path={arg:1}
            errfile synthetic-import-egl.stderr.txt
            """);
        env.Script("uninstall", """
            uninstalled
            errfile synthetic-uninstall-ok.stderr.txt
            """);
        env.Script("move", """
            installed version=1 path={arg:1}/Fortnite
            errfile synthetic-move-ok.stderr.txt
            """);
        env.Script("version", "outfile version.txt");
        using var cli = Cli(env);
        var ct = TestContext.Current.CancellationToken;

        Assert.True((await cli.ImportAsync("Fortnite", game + Path.DirectorySeparatorChar, ct)).Success);
        Assert.Equal(["-y", "import", "Fortnite", game, "--skip-dlcs"], env.Invocations()[^1].Args);

        var newBase = Path.Combine(env.Root, "Elsewhere");
        Assert.True((await cli.MoveAsync("Fortnite", newBase, skipMove: false, null, ct)).Success);
        Assert.Equal(["-y", "move", "Fortnite", newBase], env.Invocations()[^1].Args); // same volume: Legendary renames

        Assert.True((await cli.UninstallAsync("Fortnite", keepFiles: true, ct)).Success);
        Assert.Equal(["-y", "uninstall", "Fortnite", "--skip-uninstaller", "--keep-files"], env.Invocations()[^1].Args);

        Assert.Equal("0.21.1", await cli.GetVersionAsync(ct));
    }
}

public class FolderMoverTests
{
    [Fact]
    public async Task CopiesATreeWithProgress()
    {
        using var dir = new TempDir();
        var src = dir.Combine("src", "Fortnite");
        Directory.CreateDirectory(Path.Combine(src, "FortniteGame", "Content", "Paks"));
        Directory.CreateDirectory(Path.Combine(src, "Empty"));
        await File.WriteAllBytesAsync(Path.Combine(src, "FortniteGame", "Content", "Paks", "a.pak"), new byte[3 * 1024 * 1024 + 7], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(src, "root.txt"), "hello", TestContext.Current.CancellationToken);
        var reports = new List<LegendaryProgress>();
        var dst = dir.Combine("dst", "Fortnite");

        await FolderMover.CopyTreeAsync(src, dst, new SyncProgress(reports), TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Equal(FolderMover.TreeSize(src), FolderMover.TreeSize(dst));
        Assert.True(Directory.Exists(Path.Combine(dst, "Empty")));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(dst, "root.txt"), TestContext.Current.CancellationToken));
        Assert.Equal(100, reports[^1].Percent);
        Assert.Equal(2, reports[^1].TotalChunks);
    }

    [Theory]
    [InlineData("a", "a", true)]
    [InlineData("a/b", "a", true)]
    [InlineData("ab", "a", false)]
    [InlineData("a", "a/b", false)]
    public void DetectsNesting(string child, string parent, bool inside)
    {
        var root = Path.GetTempPath();
        Assert.Equal(inside, FolderMover.IsInside(Path.Combine(root, child), Path.Combine(root, parent)));
    }

    private sealed class SyncProgress(List<LegendaryProgress> list) : IProgress<LegendaryProgress>
    {
        public void Report(LegendaryProgress value) => list.Add(value);
    }
}
