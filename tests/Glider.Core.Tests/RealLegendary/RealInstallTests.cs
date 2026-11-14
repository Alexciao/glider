using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.RealLegendary;

/// <summary>
/// The writer-lane commands that work without Epic, run by Core's <see cref="LegendaryCli"/> against the real exe on a fixture install:
/// verify (ok / corrupted / missing / no manifest / no folder), uninstall, move and import. What they leave on disk is checked too.
/// </summary>
[Collection(RealLegendaryCollection.Name)]
public sealed class RealInstallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Collect : IProgress<LegendaryProgress>
    {
        public List<LegendaryProgress> Items { get; } = [];

        public void Report(LegendaryProgress value)
        {
            lock (Items)
            {
                Items.Add(value);
            }
        }
    }

    // ------------------------------------------------------------------ verify

    [RealLegendaryFact]
    public async Task VerifyOfACleanInstallSucceedsAndCountsEveryFile()
    {
        using var env = new RealLegendaryEnv();
        VerifyFixture.Install(env);
        using var cli = env.Cli();
        var progress = new Collect();

        var outcome = await cli.VerifyAsync(VerifyFixture.App, progress, Ct);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        Assert.Equal(new VerifyCounts(3, 0, 0), outcome.Counts);
        Assert.NotEmpty(progress.Items);
        Assert.Equal(100, progress.Items[^1].Percent, 0.5);
        // Legendary always writes tmp/<app>.repair: "<actual sha1>:<path>" for every file that exists (a later `repair` reuses those).
        var repair = File.ReadAllLines(Path.Combine(env.Config, "tmp", VerifyFixture.App + ".repair"));
        Assert.Equal(VerifyFixture.Files.Select(f => f.Path).Order(StringComparer.Ordinal), repair.Select(l => l[(l.IndexOf(':') + 1)..]).Order(StringComparer.Ordinal));
    }

    [RealLegendaryFact]
    public async Task VerifyReportsACorruptedFileAndLeavesLegendarysRepairList()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        var bad = VerifyFixture.FilePath(folder, "Data/assets.bin");
        var bytes = File.ReadAllBytes(bad);
        bytes[100] ^= 0xFF; // same size, one flipped byte: only the hash can notice
        File.WriteAllBytes(bad, bytes);
        using var cli = env.Cli();

        var outcome = await cli.VerifyAsync(VerifyFixture.App, new Collect(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.Corrupted, outcome.Error!.Code);
        Assert.Equal(new VerifyCounts(3, 1, 0), outcome.Counts);
        Assert.Contains("1 file(s) corrupted", outcome.Error.Detail, StringComparison.Ordinal);
        var repair = File.ReadAllLines(Path.Combine(env.Config, "tmp", VerifyFixture.App + ".repair")); // what a later `repair` trusts
        var line = Assert.Single(repair, l => l.EndsWith(":Data/assets.bin", StringComparison.Ordinal));
#pragma warning disable CA5350 // SHA-1 is Epic's manifest hash, not our security choice
        var clean = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(VerifyFixture.Pattern(70000, 11))).ToLowerInvariant();
#pragma warning restore CA5350
        Assert.NotEqual(clean, line[..line.IndexOf(':')]); // the corrupt file is listed with its actual hash, so repair re-downloads its chunks
    }

    [RealLegendaryFact]
    public async Task VerifyReportsAMissingFileAndASecondRunAfterRestoringItIsClean()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        var notes = VerifyFixture.FilePath(folder, "Data/notes.txt");
        File.Delete(notes);
        using var cli = env.Cli();

        var broken = await cli.VerifyAsync(VerifyFixture.App, new Collect(), Ct);

        Assert.False(broken.Success);
        Assert.Equal(ErrorCode.Corrupted, broken.Error!.Code);
        Assert.Equal(new VerifyCounts(3, 0, 1), broken.Counts);
        Assert.DoesNotContain("notes.txt", File.ReadAllText(Path.Combine(env.Config, "tmp", VerifyFixture.App + ".repair")), StringComparison.Ordinal); // missing = not listed

        File.WriteAllBytes(notes, VerifyFixture.Pattern(150, 13));
        var fixedUp = await cli.VerifyAsync(VerifyFixture.App, new Collect(), Ct);
        Assert.True(fixedUp.Success, fixedUp.Error?.Detail);
        Assert.Equal(new VerifyCounts(3, 0, 0), fixedUp.Counts);
    }

    [RealLegendaryFact]
    public async Task VerifyWithoutLegendarysManifestCopyIsCorruptedNotACrash()
    {
        using var env = new RealLegendaryEnv();
        VerifyFixture.Install(env);
        File.Delete(Path.Combine(env.Config, "manifests", VerifyFixture.ManifestName));
        using var cli = env.Cli();

        var outcome = await cli.VerifyAsync(VerifyFixture.App, new Collect(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.Corrupted, outcome.Error!.Code);
        Assert.Contains("manifest", outcome.Error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [RealLegendaryFact]
    public async Task VerifyOfAFolderThatWasDeletedBehindLegendarysBackIsNotInstalled()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        Directory.Delete(folder, recursive: true);
        using var cli = env.Cli();

        var outcome = await cli.VerifyAsync(VerifyFixture.App, new Collect(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.NotInstalled, outcome.Error!.Code);
    }

    [RealLegendaryFact]
    public async Task VerifyOfAGameLegendaryDoesNotKnowIsNotInstalled()
    {
        using var env = new RealLegendaryEnv();
        using var cli = env.Cli();

        var outcome = await cli.VerifyAsync("NoSuchGame", new Collect(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.NotInstalled, outcome.Error!.Code);
    }

    // ------------------------------------------------------------------ uninstall

    [RealLegendaryFact]
    public async Task UninstallKeepFilesForgetsTheGameAndTouchesNoFile()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        var before = Snapshot(folder);
        using var cli = env.Cli();

        var outcome = await cli.UninstallAsync(VerifyFixture.App, keepFiles: true, Ct);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        Assert.Equal(before, Snapshot(folder)); // every byte still there
        Assert.Null(await cli.GetInstalledAsync(VerifyFixture.App, Ct));
        Assert.Empty(VerifyFixture.ReadInstalled(env));

        var again = await cli.UninstallAsync(VerifyFixture.App, keepFiles: true, Ct);
        Assert.True(again.Success, again.Error?.Detail); // already gone is success (idempotent)
    }

    [RealLegendaryFact]
    public async Task UninstallRemovesTheManifestFilesAndNothingTheUserPutThere()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        var userFile = Path.Combine(folder, "Saved", "slot1.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        File.WriteAllText(userFile, "precious");
        using var cli = env.Cli();

        var outcome = await cli.UninstallAsync(VerifyFixture.App, keepFiles: false, Ct);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        foreach (var (path, _, _) in VerifyFixture.Files)
        {
            Assert.False(File.Exists(VerifyFixture.FilePath(folder, path)), path);
        }

        Assert.True(File.Exists(userFile)); // files the manifest doesn't list survive an uninstall
        Assert.Equal("precious", File.ReadAllText(userFile));
        Assert.Null(await cli.GetInstalledAsync(VerifyFixture.App, Ct));
    }

    // ------------------------------------------------------------------ move

    [RealLegendaryFact]
    public async Task MoveSkipMoveOnlyRewritesLegendarysPointer()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        var newBase = Path.Combine(env.Root, "NewBase");
        Directory.CreateDirectory(newBase);
        var before = Snapshot(folder);
        using var cli = env.Cli();

        var outcome = await cli.MoveAsync(VerifyFixture.App, newBase, skipMove: true, null, Ct);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        Assert.Equal(before, Snapshot(folder)); // nothing moved
        Assert.Empty(Directory.EnumerateFileSystemEntries(newBase));
        var installed = await cli.GetInstalledAsync(VerifyFixture.App, Ct);
        Assert.Equal(Path.Combine(newBase, VerifyFixture.App), installed!.InstallPath);
    }

    [RealLegendaryFact]
    public async Task MoveOnTheSameVolumeMovesTheFolderAndLegendaryFollows()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        var newBase = Path.Combine(env.Root, "OtherPlace");
        Directory.CreateDirectory(newBase);
        var before = Snapshot(folder);
        using var cli = env.Cli();

        var outcome = await cli.MoveAsync(VerifyFixture.App, newBase, skipMove: false, null, Ct);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        var target = Path.Combine(newBase, VerifyFixture.App);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(before.Select(f => (f.Name, f.Hash)), Snapshot(target).Select(f => (f.Name, f.Hash)));
        Assert.Equal(target, (await cli.GetInstalledAsync(VerifyFixture.App, Ct))!.InstallPath);
        var verify = await cli.VerifyAsync(VerifyFixture.App, new Collect(), Ct); // Legendary's own check agrees with the new place
        Assert.True(verify.Success, verify.Error?.Detail);
    }

    [RealLegendaryFact]
    public async Task MoveOfAnUnknownGameIsNotInstalled()
    {
        using var env = new RealLegendaryEnv();
        using var cli = env.Cli();

        var outcome = await cli.MoveAsync("NoSuchGame", env.Root, skipMove: true, null, Ct);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.NotInstalled, outcome.Error!.Code);
    }

    // ------------------------------------------------------------------ import

    [RealLegendaryFact]
    public async Task ImportOfAMissingFolderIsAnInvalidFolderBeforeAnyLogin()
    {
        using var env = new RealLegendaryEnv();
        using var cli = env.Cli();

        var outcome = await cli.ImportAsync(VerifyFixture.App, Path.Combine(env.Root, "nowhere"), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.InvalidFolder, outcome.Error!.Code);
    }

    [RealLegendaryFact]
    public async Task ImportWhileSignedOutAsksToSignInInsteadOfCrashing()
    {
        using var env = new RealLegendaryEnv();
        var folder = Path.Combine(env.Games, "Existing");
        VerifyFixture.WriteFiles(folder);
        using var cli = env.Cli();

        var outcome = await cli.ImportAsync(VerifyFixture.App, folder, Ct);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.NotSignedIn, outcome.Error!.Code);
        Assert.Equal(FixAction.SignIn, outcome.Error.Fix);
    }

    [RealLegendaryFact]
    public async Task ImportOfAGameThatIsAlreadyInstalledIsAlreadyInstalled()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        using var cli = env.Cli();

        var outcome = await cli.ImportAsync(VerifyFixture.App, folder, Ct);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.AlreadyInstalled, outcome.Error!.Code);
    }

    private static List<(string Name, string Hash)> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(folder, f), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))))
            .ToList();
}
