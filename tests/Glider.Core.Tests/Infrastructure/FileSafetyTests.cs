using System.Diagnostics;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Jobs;
using Glider.Core.Legendary;
using Glider.Core.Tests.Jobs;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Infrastructure;

/// <summary>Security review: Glider's own recursive deletes can never reach anything but a game folder.</summary>
public class DeleteGuardTests
{
    [Fact]
    public void RefusesRootsAndSystemOrUserFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotNull(DeleteGuard.Refusal(Path.GetPathRoot(Path.GetTempPath())));
        Assert.NotNull(DeleteGuard.Refusal(profile));
        Assert.NotNull(DeleteGuard.Refusal(Path.GetDirectoryName(profile))); // C:\Users contains the profile
        Assert.NotNull(DeleteGuard.Refusal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        Assert.NotNull(DeleteGuard.Refusal(AppContext.BaseDirectory));
        Assert.NotNull(DeleteGuard.Refusal("relative\\Fortnite"));
        Assert.NotNull(DeleteGuard.Refusal(""));
    }

    [Fact]
    public void DotDotSegmentsCannotEscapeTheCheck()
    {
        using var dir = new TempDir();
        var sneaky = dir.Combine("Games", "Fortnite") + new string(Path.DirectorySeparatorChar, 1) +
                     string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("..", 30));
        Assert.NotNull(DeleteGuard.Refusal(sneaky)); // resolves to the drive root
    }

    [Fact]
    public void AllowsAnOrdinaryGameFolderAndHonoursExtraProtectedFolders()
    {
        using var dir = new TempDir();
        var game = dir.Combine("Games", "Fortnite");
        Directory.CreateDirectory(game);
        Assert.Null(DeleteGuard.Refusal(game));
        Assert.NotNull(DeleteGuard.Refusal(dir.Combine("Games"), game)); // contains a protected folder
    }

    [Fact]
    public void RefusesAJunctionAndNeverFollowsJunctionsInsideTheTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var precious = dir.Combine("Precious");
        Directory.CreateDirectory(precious);
        File.WriteAllText(Path.Combine(precious, "keep.txt"), "mine");
        var game = dir.Combine("Games", "Fortnite");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "a.pak"), "x");
        MakeJunction(Path.Combine(game, "link"), precious);
        var topLink = dir.Combine("Games", "LinkedFortnite");
        MakeJunction(topLink, precious);

        var log = new TestLog();
        Assert.False(DeleteGuard.TryDeleteTree(topLink, log));
        Assert.True(Directory.Exists(topLink));

        Assert.True(DeleteGuard.TryDeleteTree(game, log), string.Join(" | ", log.Lines));
        Assert.False(Directory.Exists(game));
        Assert.True(File.Exists(Path.Combine(precious, "keep.txt"))); // the junction was removed, not followed
    }

    internal static void MakeJunction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "mklink /J failed");
    }
}

public class CancelCleanupSafetyTests
{
    [Fact]
    public async Task ATamperedGameFolderInJobsJsonIsNeverDeleted()
    {
        await using var h = new QueueHarness(legendary: new StubLegendary(verifySucceeds: true));
        var precious = Path.Combine(h.Env.Root, "Precious");
        Directory.CreateDirectory(precious);
        await File.WriteAllTextAsync(Path.Combine(precious, "keep.txt"), "mine", TestContext.Current.CancellationToken);

        // A paused install whose persisted record claims it created a folder it would never have created.
        var file = new PersistedJobFile
        {
            Jobs =
            [
                new PersistedJob
                {
                    Info = new JobInfo("x", JobKind.Install, JobStatus.Paused, null, null, DateTimeOffset.UtcNow, null, h.Games, null, null, null),
                    Request = new JobRequest(JobKind.Install, h.Games, null, null, null),
                    CreatedGameFolder = true,
                    GameFolder = precious,
                },
            ],
        };
        Directory.CreateDirectory(h.Env.Paths.State);
        await File.WriteAllBytesAsync(JobQueue.PathFor(h.Env.Paths), JsonSerializer.SerializeToUtf8Bytes(file, EngineJsonContext.Default.PersistedJobFile), TestContext.Current.CancellationToken);

        var q = await h.StartAsync();
        await q.CancelAsync("x");
        await q.WhenCleanupDoneAsync();
        Assert.True(File.Exists(Path.Combine(precious, "keep.txt")));
        Assert.True(h.Env.Log.Contains("Not removing"));
    }
}

public class CrossDriveMoveSafetyTests
{
    private const string CrossDrive = "err [cli] ERROR: Moving to a different drive is not supported. Move the folder manually to \"x\" and run \"legendary move Fortnite \"x\" --skip-move\"";

    private static (FakeLegendaryEnv Env, LegendaryCli Cli, string Source, string NewBase) Setup(bool realGame = true)
    {
        var env = new FakeLegendaryEnv();
        env.UseFixture("synthetic-move-ok.stderr.txt");
        env.UseFixture("synthetic-import-egl.stderr.txt");
        var source = Path.Combine(env.Root, "Games", "Fortnite");
        Directory.CreateDirectory(source);
        if (realGame)
        {
            var exe = FortniteLayout.ShippingExe(source);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "exe");
        }

        File.WriteAllText(Path.Combine(source, "user.ini"), "settings");
        env.Script("import", "installed version=1 path={arg:1}\nerrfile synthetic-import-egl.stderr.txt");
        env.Script("move", CrossDrive, invocation: 1);
        env.Script("move", "installed version=1 path={arg:1}/Fortnite\nerrfile synthetic-move-ok.stderr.txt");
        var cli = new LegendaryCli(env.Paths, env.Log);
        return (env, cli, source, Path.Combine(env.Root, "Other"));
    }

    [Fact]
    public async Task RefusesToCopyAndDeleteAFolderWithoutTheGame()
    {
        var (env, cli, source, newBase) = Setup(realGame: false);
        using (env)
        using (cli)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.True((await cli.ImportAsync("Fortnite", source, ct)).Success);
            var outcome = await cli.MoveAsync("Fortnite", newBase, skipMove: false, null, ct);
            Assert.False(outcome.Success);
            Assert.Equal(ErrorCode.InvalidFolder, outcome.Error!.Code);
            Assert.True(File.Exists(Path.Combine(source, "user.ini")));
        }
    }

    [Fact]
    public async Task APartialCopyFromAnInterruptedMoveIsDiscardedAndTheMoveCompletes()
    {
        var (env, cli, source, newBase) = Setup();
        using (env)
        using (cli)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.True((await cli.ImportAsync("Fortnite", source, ct)).Success);
            var destination = Path.Combine(newBase, "Fortnite");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, FolderMover.PartialMarker), "");
            File.WriteAllText(Path.Combine(destination, "half.pak"), "torn");

            var outcome = await cli.MoveAsync("Fortnite", newBase, skipMove: false, null, ct);
            Assert.True(outcome.Success, outcome.Error?.Detail);
            Assert.False(Directory.Exists(source));
            Assert.True(File.Exists(Path.Combine(destination, "user.ini")));
            Assert.False(File.Exists(Path.Combine(destination, "half.pak")));
            Assert.False(File.Exists(Path.Combine(destination, FolderMover.PartialMarker)));
        }
    }

    [Fact]
    public async Task AnUnrelatedNonEmptyDestinationIsLeftAlone()
    {
        var (env, cli, source, newBase) = Setup();
        using (env)
        using (cli)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.True((await cli.ImportAsync("Fortnite", source, ct)).Success);
            var destination = Path.Combine(newBase, "Fortnite");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "someone-elses.txt"), "keep");

            var outcome = await cli.MoveAsync("Fortnite", newBase, skipMove: false, null, ct);
            Assert.False(outcome.Success);
            Assert.True(File.Exists(Path.Combine(destination, "someone-elses.txt")));
            Assert.True(File.Exists(Path.Combine(source, "user.ini")));
        }
    }
}

public class PreferredCdnArgumentTests
{
    [Theory]
    [InlineData("-y")]
    [InlineData("--base-path")]
    [InlineData(".fastly")]
    public async Task AFlagLookingCdnIsDroppedNotPassedToLegendary(string cdn)
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, "legendary.exe");
        using var store = await SettingsStore.LoadAsync(paths, new TestLog(), TestContext.Current.CancellationToken);
        var saved = await store.UpdateAsync(Settings.Default with { PreferredCdn = cdn });
        Assert.Null(saved.PreferredCdn);

        var args = LegendaryCli.InstallArguments(new InstallArgs("Fortnite", InstallMode.Update, null, 0, 0, cdn), installed: true, 0, false);
        Assert.DoesNotContain("--preferred-cdn", args);
    }
}
