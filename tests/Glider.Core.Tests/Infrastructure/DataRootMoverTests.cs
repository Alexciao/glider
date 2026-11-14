using System.Security.Cryptography;
using System.Text.Json;
using Glider.Core.Infrastructure.Migrations;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Infrastructure;

/// <summary>
/// Schema 3: the data root moves from %LocalAppData%\Glider (Velopack's install root) to %LocalAppData%\GliderData, here over a
/// fake %LocalAppData% in a temp folder. Same-volume moves are real renames (and real locks); another volume is simulated by a
/// file system that refuses renames between the two roots with ERROR_NOT_SAME_DEVICE, as Windows does.
/// </summary>
public sealed class DataRootMoverTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly MoveLayout _l;

    public DataRootMoverTests() => _l = new MoveLayout(_dir.Combine("LocalAppData"));

    private string Old => _l.Old;

    private string New => _l.New;

    internal static DataRootMoverOptions Options(Action<string>? step = null, FakeOverlayRegistry? registry = null, bool allowCopy = true) =>
        new() { LockRetryDelays = [], Step = step, OverlayRegistry = registry, AllowCopy = allowCopy, Time = new ManualTimeProvider() };

    private DataRootMoveResult Run(DataRootMoverOptions? options = null, DataRootFileSystem? fs = null) =>
        new DataRootMover(Old, New, options ?? Options(), fs).Run();

    // ------------------------------------------------------------------ the move

    [Fact]
    public void SameVolume_RenamesEveryDataDirectory_AndLeavesVelopackAlone()
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();
        var steps = new List<string>();

        var result = Run(Options(steps.Add));

        Assert.Equal(DataRootMoveStatus.Moved, result.Status);
        Assert.Equal(New, result.Root);
        Assert.True(result.CanStart);
        _l.AssertMoved(original, velopack);
        Assert.Equal([.. DataRoot.Directories.Select(d => "move:" + d), "marker", "fixups", "pointer", "finished"], steps);
        Assert.Contains(result.Messages, m => m.Contains("Moved Glider's data", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherVolume_CopiesVerifiesAndDeletesTheSourceOnlyAfterTheMarker()
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();
        var fs = new FakeFs(Old, New) { CrossVolume = true };
        var sourcesAtMarker = new List<bool>();
        var pendingAtMarker = -1;
        var steps = new List<string>();

        var result = Run(Options(s =>
        {
            steps.Add(s);
            if (s == "marker")
            {
                sourcesAtMarker.AddRange(DataRoot.Directories.Select(d => Directory.Exists(Path.Combine(Old, d))));
                pendingAtMarker = DataRoot.ReadMarker(New)!.PendingDeletes.Count;
            }
        }), fs);

        Assert.Equal(DataRootMoveStatus.Moved, result.Status);
        Assert.Equal(DataRoot.Directories.Length, sourcesAtMarker.Count);
        Assert.All(sourcesAtMarker, Assert.True); // nothing deleted before the commit
        Assert.Equal(DataRoot.Directories.Length, pendingAtMarker);
        _l.AssertMoved(original, velopack);
        foreach (var d in DataRoot.Directories)
        {
            Assert.True(steps.IndexOf("copy:" + d) < steps.IndexOf("verify:" + d) && steps.IndexOf("verify:" + d) < steps.IndexOf("move:" + d), d);
            Assert.True(steps.IndexOf("pointer") < steps.IndexOf("delete:" + d), d); // the pointer is written before any source goes
        }

        Assert.Contains(result.Messages, m => m.Contains("copied and verified", StringComparison.Ordinal));
    }

    [Fact]
    public void AResumeFileOfADownloadInProgressSurvivesByteForByte()
    {
        _l.Seed();
        var resume = File.ReadAllBytes(Path.Combine(Old, "legendary", "tmp", "Fortnite.resume"));

        Run();

        Assert.Equal(resume, File.ReadAllBytes(Path.Combine(New, "legendary", "tmp", "Fortnite.resume")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACrashAfterAnyStep_IsCompletedByTheNextStart(bool crossVolume)
    {
        // Every step of a clean run on this kind of volume.
        var steps = new List<string>();
        var probe = new MoveLayout(_dir.Combine("probe", "LocalAppData"));
        probe.Seed();
        new DataRootMover(probe.Old, probe.New, Options(steps.Add), new FakeFs(probe.Old, probe.New) { CrossVolume = crossVolume }).Run();
        Assert.True(steps.Count >= DataRoot.Directories.Length + 4);

        foreach (var crashAt in steps)
        {
            var t = new MoveLayout(_dir.Combine("crash-" + crashAt.Replace(':', '-'), "LocalAppData"));
            var original = t.Seed();
            var velopack = t.VelopackHashes();
            var fs = new FakeFs(t.Old, t.New) { CrossVolume = crossVolume };

            Assert.Throws<SimulatedCrashException>(() => new DataRootMover(t.Old, t.New, Options(s =>
            {
                if (s == crashAt)
                {
                    throw new SimulatedCrashException(s);
                }
            }), fs).Run());
            t.AssertNothingLost(original);

            var resumed = new DataRootMover(t.Old, t.New, Options(), fs).Run();

            Assert.True(resumed.Status is DataRootMoveStatus.Moved or DataRootMoveStatus.AlreadyMoved, $"{crashAt}: {resumed.Status}");
            Assert.Equal(t.New, resumed.Root);
            t.AssertMoved(original, velopack);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrashingAtEveryStepInTurn_StillConverges(bool crossVolume)
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();
        var fs = new FakeFs(Old, New) { CrossVolume = crossVolume };
        var passed = new HashSet<string>();
        var starts = 0;
        while (true)
        {
            Assert.True(++starts < 50, "the move never finished");
            try
            {
                Run(Options(s =>
                {
                    if (passed.Add(s))
                    {
                        throw new SimulatedCrashException(s); // each start dies right after the first step it had not done before
                    }
                }), fs);
                break;
            }
            catch (SimulatedCrashException)
            {
                _l.AssertNothingLost(original);
            }
        }

        Assert.True(starts > DataRoot.Directories.Length);
        _l.AssertMoved(original, velopack);
    }

    [Fact]
    public void ALockedFile_PutsEverythingBack_UsesTheOldRoot_AndMovesAtTheNextStart()
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();
        var resume = Path.Combine(Old, "legendary", "tmp", "Fortnite.resume");

        // An orphan Legendary still writing its resume file: Windows refuses to rename a folder with an open file inside.
        DataRootMoveResult deferred;
        using (new FileStream(resume, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            deferred = Run();
        }

        Assert.Equal(DataRootMoveStatus.Deferred, deferred.Status);
        Assert.Equal(Old, deferred.Root);
        Assert.True(deferred.CanStart);
        Assert.NotNull(deferred.Error);
        Assert.Contains(deferred.Messages, m => m.Contains("retried at the next start", StringComparison.Ordinal));
        _l.AssertUntouchedOldRoot(original, velopack); // webview2 had already moved: it was put back

        var next = Run();

        Assert.Equal(DataRootMoveStatus.Moved, next.Status);
        _l.AssertMoved(original, velopack);
    }

    [Fact]
    public void ALockReleasedWhileRetrying_DoesNotDeferTheMove()
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();
        var handle = new FileStream(Path.Combine(Old, "webview2", "EBWebView", "lockfile"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var fs = new FakeFs(Old, New) { OnSleep = _ => handle.Dispose() }; // the previous session's browser process exits

        var result = new DataRootMover(Old, New, new DataRootMoverOptions { LockRetryDelays = [TimeSpan.Zero], Time = new ManualTimeProvider() }, fs).Run();

        Assert.Equal(DataRootMoveStatus.Moved, result.Status);
        Assert.Equal(1, fs.Sleeps);
        _l.AssertMoved(original, velopack);
    }

    [Fact]
    public void ALockedFileOnAnotherVolume_DiscardsTheCopies_AndLeavesTheSourceIntact()
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();
        var fs = new FakeFs(Old, New) { CrossVolume = true };

        DataRootMoveResult deferred;
        using (new FileStream(Path.Combine(Old, "state", "saves.json"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            deferred = Run(fs: fs);
        }

        Assert.Equal(DataRootMoveStatus.Deferred, deferred.Status);
        _l.AssertUntouchedOldRoot(original, velopack);
        Assert.Equal(DataRootMoveStatus.Moved, Run(fs: fs).Status);
        _l.AssertMoved(original, velopack);
    }

    [Fact]
    public void ACopyThatDoesNotMatchItsSource_IsNeverCommitted()
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();
        var fs = new FakeFs(Old, New)
        {
            CrossVolume = true,
            AfterCopy = target =>
            {
                if (target.EndsWith(Path.Combine("state", "games.json"), StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = File.ReadAllBytes(target);
                    bytes[0] ^= 0xFF; // same size, different bytes: only the hash check sees it
                    File.WriteAllBytes(target, bytes);
                }
            },
        };

        var result = Run(fs: fs);

        Assert.Equal(DataRootMoveStatus.Deferred, result.Status);
        Assert.Contains("differs from the original", result.Error!.Message, StringComparison.Ordinal);
        _l.AssertUntouchedOldRoot(original, velopack);
    }

    [Fact]
    public void WhenPuttingBackFails_GliderMustNotStart_AndTheNextStartFinishesTheMove()
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();
        var legendary = Path.Combine(Old, "legendary");
        var fs = new FakeFs(Old, New)
        {
            OnMove = (source, target) =>
                source == legendary ? new IOException("in use") :
                target.StartsWith(Old + "\\", StringComparison.OrdinalIgnoreCase) ? new IOException("antivirus") : null,
        };

        var failed = Run(fs: fs);

        Assert.Equal(DataRootMoveStatus.Failed, failed.Status);
        Assert.False(failed.CanStart);
        Assert.Contains(failed.Messages, m => m.Contains("will not start", StringComparison.Ordinal));
        _l.AssertNothingLost(original);
        Assert.True(Directory.Exists(Path.Combine(New, "webview2"))); // really split

        fs.OnMove = null;
        Assert.Equal(DataRootMoveStatus.Moved, Run(fs: fs).Status);
        _l.AssertMoved(original, velopack);
    }

    [Fact]
    public void RunningAgainChangesNothing()
    {
        _l.Seed();
        Assert.Equal(DataRootMoveStatus.Moved, Run().Status);
        var newRoot = MoveLayout.AllHashes(New);
        var oldRoot = MoveLayout.AllHashes(Old);

        var again = Run();

        Assert.Equal(DataRootMoveStatus.AlreadyMoved, again.Status);
        Assert.Equal(New, again.Root);
        Assert.Equal(newRoot, MoveLayout.AllHashes(New));
        Assert.Equal(oldRoot, MoveLayout.AllHashes(Old));
    }

    [Fact]
    public void ADowngradeRecreatingTheOldFolders_IsIgnoredOnceCommitted()
    {
        _l.Seed();
        Run();
        MoveLayout.Write(Old, "state/games.json", "written by an older build"); // a downgraded build started fresh in the old root

        var again = Run();

        Assert.Equal(DataRootMoveStatus.AlreadyMoved, again.Status);
        Assert.Equal("written by an older build", File.ReadAllText(Path.Combine(Old, "state", "games.json")));
        Assert.Equal(New, DataRoot.Resolve(Old, New));
    }

    [Fact]
    public void AnExplicitDataDir_IsUsedAsItIs_AndNothingMoves()
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();
        var explicitRoot = _dir.Combine("portable-data");
        MoveLayout.Write(explicitRoot, "state/games.json", "{}");

        var result = DataRoot.Prepare(explicitRoot, Options(), _l.LocalAppData);

        Assert.Equal(DataRootMoveStatus.NotNeeded, result.Status);
        Assert.Equal(explicitRoot, result.Root);
        Assert.Equal(original, MoveLayout.DataHashes(Old));
        Assert.Equal(velopack, _l.VelopackHashes());
        Assert.False(Directory.Exists(New));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(explicitRoot, "state", "games.json")));
        Assert.False(File.Exists(Path.Combine(explicitRoot, DataRoot.MarkerFile)));
    }

    [Fact]
    public void PrepareMovesThePerUserRoot()
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();

        var result = DataRoot.Prepare(null, Options(), _l.LocalAppData);

        Assert.Equal(DataRootMoveStatus.Moved, result.Status);
        Assert.Equal(New, result.Root);
        _l.AssertMoved(original, velopack);
    }

    [Fact]
    public void AFreshInstall_CommitsTheNewRoot_WithoutAPointer()
    {
        _l.SeedVelopackOnly();

        var result = Run();

        Assert.Equal(DataRootMoveStatus.Fresh, result.Status);
        Assert.Equal(New, result.Root);
        var marker = DataRoot.ReadMarker(New)!;
        Assert.Null(marker.MovedFrom);
        Assert.True(marker.Finished);
        Assert.False(File.Exists(Path.Combine(Old, DataRoot.PointerFile)));
        Assert.Equal(DataRootMoveStatus.AlreadyMoved, Run().Status);
        Assert.Equal(New, DataRoot.Resolve(Old, New));
    }

    [Fact]
    public void APointerWithoutAMarker_NeverMovesOldLeftoversOver()
    {
        MoveLayout.Write(Old, "state/games.json", "stale leftover");
        MoveLayout.Write(Path.Combine(Old, DataRoot.PointerFile), "moved");

        var result = Run();

        Assert.Equal(DataRootMoveStatus.Fresh, result.Status);
        Assert.Equal(New, result.Root);
        Assert.Equal("stale leftover", File.ReadAllText(Path.Combine(Old, "state", "games.json")));
        Assert.False(Directory.Exists(Path.Combine(New, "state")));
        Assert.Contains(result.Messages, m => m.Contains("is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void AReinstall_GetsThePointerBack()
    {
        _l.Seed();
        Run();
        File.Delete(Path.Combine(Old, DataRoot.PointerFile)); // uninstall removed the old root, a reinstall recreated it

        Assert.Equal(DataRootMoveStatus.AlreadyMoved, Run().Status);
        Assert.True(File.Exists(Path.Combine(Old, DataRoot.PointerFile)));
    }

    [Fact]
    public void TheOverlayRegistrationFollowsTheOverlayFolder()
    {
        _l.Seed();
        var registry = new FakeOverlayRegistry { Path = Path.Combine(Old, "overlay").Replace('\\', '/') };

        Run(Options(registry: registry));

        Assert.Equal(["disable", "enable:" + Path.Combine(New, "overlay")], registry.Calls);
        Assert.Equal(Path.Combine(New, "overlay").Replace('\\', '/'), registry.Path);
    }

    [Fact]
    public void AnOverlayRegisteredElsewhere_IsLeftAlone()
    {
        _l.Seed();
        const string epic = "C:/Program Files (x86)/Epic Games/Launcher/Portal/Extras/Overlay";
        var registry = new FakeOverlayRegistry { Path = epic };

        Run(Options(registry: registry));

        Assert.Empty(registry.Calls);
        Assert.Equal(epic, registry.Path);
    }

    [Fact]
    public void RenamesOnly_DefersWhenTheDataIsOnAnotherVolume()
    {
        var original = _l.Seed();
        var velopack = _l.VelopackHashes();

        var result = Run(Options(allowCopy: false), new FakeFs(Old, New) { CrossVolume = true });

        Assert.Equal(DataRootMoveStatus.Deferred, result.Status);
        _l.AssertUntouchedOldRoot(original, velopack);
    }

    [Fact]
    public void OnlyPathsIntoTheOldDataFoldersAreRepointed()
    {
        var mover = new DataRootMover(Old, New);
        Assert.Equal(Path.Combine(New, "overlay"), mover.Repoint(Path.Combine(Old, "overlay"), Old));
        Assert.Equal(Path.Combine(New, "state", "x.json"), mover.Repoint(Old.Replace('\\', '/') + "/state/x.json", Old));
        Assert.Null(mover.Repoint(Path.Combine(Old, "current", "legendary", "legendary.exe"), Old)); // Velopack's
        Assert.Null(mover.Repoint(Old + "Data\\state", Old)); // GliderData itself is not "inside" Glider
        Assert.Null(mover.Repoint(@"D:\Games\Fortnite", Old));
    }

    public void Dispose() => _dir.Dispose();
}

/// <summary>A fake %LocalAppData%: an old root with Velopack's files and every data directory, and the checks on both roots.</summary>
internal sealed class MoveLayout(string localAppData)
{
    private const string OverlayDll = "EOSOVH-Win64-Shipping.dll";

    /// <summary>Files rewritten by the post-commit path fix-ups: compared by content, not by hash.</summary>
    public static readonly string[] FixedUp = [Path.Combine("legendary", "overlay_install.json"), Path.Combine("state", "jobs.json")];

    /// <summary>Velopack's own files in the install root: never touched.</summary>
    public static readonly string[] VelopackFiles =
    [
        "Update.exe", "Glider.exe", ".portable", "sq.version", Path.Combine("current", "Glider.exe"), Path.Combine("current", "legendary", "legendary.exe"),
        Path.Combine("current", "sq.version"), Path.Combine("packages", "Glider-0.2.0-full.nupkg"),
    ];

    private static readonly EnumerationOptions Everything = new() { RecurseSubdirectories = true, AttributesToSkip = 0 };

    public string LocalAppData { get; } = localAppData;

    public string Old { get; } = DataRoot.LegacyRootFor(localAppData);

    public string New { get; } = DataRoot.RootFor(localAppData);

    public void SeedVelopackOnly()
    {
        foreach (var file in VelopackFiles)
        {
            Write(Path.Combine(Old, file), "velopack:" + file);
        }
    }

    /// <summary>A realistic old root: Velopack's files plus every data directory, a resume file mid-download, hidden and empty entries.</summary>
    public Dictionary<string, string> Seed()
    {
        SeedVelopackOnly();
        Write(Old, "legendary/user.json", """{"displayName":"Player","access_token":"secret"}""");
        Write(Old, "legendary/installed.json", """{"Fortnite":{"install_path":"D:\\Games\\Fortnite","version":"43.10"}}""");
        Write(Old, "legendary/tmp/Fortnite.resume", "a1b2c3:FortniteGame/Content/Paks/pakchunk0.pak\n");
        Write(Old, "legendary/manifests/Fortnite.manifest", new string('m', 70_000));
        Write(Old, "legendary/overlay_install.json", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["app_name"] = "98bc04bc842e4906993fd6d6644ffb8d",
            ["install_path"] = Path.Combine(Old, "overlay"),
            ["version"] = "1.0",
        }));
        Write(Old, "overlay/" + OverlayDll, "dll");
        Write(Old, "state/schema.json", """{"schemaVersion":2,"migratedFrom":null,"migratedAt":"2026-11-01T00:00:00+00:00"}""");
        Write(Old, "state/games.json", """{"schemaVersion":2,"games":{"Fortnite":{"playtimeSeconds":3600}}}""");
        Write(Old, "state/saves.json", """{"schemaVersion":2,"games":{"Fortnite":{"files":[]}}}""");
        Write(Old, "state/jobs.json", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schemaVersion"] = 2,
            ["jobs"] = new[] { new Dictionary<string, object> { ["request"] = new Dictionary<string, string> { ["appName"] = "__overlay", ["targetPath"] = Path.Combine(Old, "overlay").Replace('\\', '/') }, ["gameFolder"] = @"D:\Games\X" } },
        }));
        Write(Old, "backups/saves/Fortnite/20261101-120000-000/ClientSettings.sav", "only copy of an overwritten save");
        Write(Old, "backups/egl/20261101-120000-000/Manifests/abc.item", """{"AppName":"Fortnite"}""");
        Write(Old, "cache/library.json", "[]");
        Write(Old, "cache/art/f8a782b6ac2b3547.jpg", "jpg");
        Directory.CreateDirectory(Path.Combine(Old, "cache", "art", "empty"));
        Write(Old, "logs/glider-20261113.log", "log line");
        Write(Old, "webview2/EBWebView/GliderEpic/Network/Cookies", "epic session cookie");
        Write(Old, "webview2/EBWebView/lockfile", "");
        File.SetAttributes(Path.Combine(Old, "webview2", "EBWebView", "lockfile"), FileAttributes.Hidden);
        Write(Old, "placeholder/index.html", "<html></html>");
        return DataHashes(Old);
    }

    public static void Write(string root, string relative, string content) => Write(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), content);

    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static string Hash(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    /// <summary>Hashes of every file in the data directories of <paramref name="root"/>, by path relative to the root.</summary>
    public static Dictionary<string, string> DataHashes(string root) => DataRoot.Directories
        .Select(d => Path.Combine(root, d))
        .Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateFiles(d, "*", Everything))
        .ToDictionary(f => Path.GetRelativePath(root, f), Hash, StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string> AllHashes(string root) =>
        Directory.EnumerateFiles(root, "*", Everything).ToDictionary(f => Path.GetRelativePath(root, f), Hash, StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> VelopackHashes() => VelopackFiles.ToDictionary(f => f, f => Hash(Path.Combine(Old, f)));

    /// <summary>The finished layout: everything in the new root (path fix-ups applied); the old root holds Velopack's files and the pointer.</summary>
    public void AssertMoved(Dictionary<string, string> original, Dictionary<string, string> velopack)
    {
        var marker = DataRoot.ReadMarker(New)!;
        Assert.Equal(3, marker.SchemaVersion);
        Assert.Equal(Old, marker.MovedFrom);
        Assert.True(marker.Finished);
        Assert.Empty(marker.PendingDeletes);
        Assert.Equal(DataRoot.Directories.Order(), marker.Directories.Order());

        Assert.All(DataRoot.Directories, d => Assert.False(Directory.Exists(Path.Combine(Old, d)), d + " is still in the old root"));
        Assert.False(Directory.Exists(Path.Combine(New, DataRoot.StagingFolder)));
        Assert.True(Directory.Exists(Path.Combine(New, "cache", "art", "empty")));

        var moved = DataHashes(New);
        Assert.Equal(original.Keys.Order(StringComparer.OrdinalIgnoreCase), moved.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (file, hash) in original.Where(kv => !FixedUp.Contains(kv.Key)))
        {
            Assert.Equal(hash, moved[file]);
        }

        // The absolute paths into the old root now point into the new one.
        var overlay = JsonDocument.Parse(File.ReadAllText(Path.Combine(New, "legendary", "overlay_install.json"))).RootElement;
        Assert.Equal(Path.Combine(New, "overlay"), overlay.GetProperty("install_path").GetString());
        Assert.Equal("1.0", overlay.GetProperty("version").GetString());
        var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(New, "state", "jobs.json"))).RootElement.GetProperty("jobs")[0];
        Assert.Equal(Path.Combine(New, "overlay"), job.GetProperty("request").GetProperty("targetPath").GetString());
        Assert.Equal(@"D:\Games\X", job.GetProperty("gameFolder").GetString()); // not ours: untouched

        // Velopack's files are byte-identical and the pointer is the only new file in the old root.
        Assert.Equal(velopack, VelopackHashes());
        var others = Directory.EnumerateFiles(Old, "*", Everything).Select(f => Path.GetRelativePath(Old, f)).Except(VelopackFiles).ToList();
        Assert.Equal([DataRoot.PointerFile], others);
        Assert.Contains(New, File.ReadAllText(Path.Combine(Old, DataRoot.PointerFile)), StringComparison.Ordinal);

        Assert.Equal(New, DataRoot.Resolve(Old, New));
    }

    /// <summary>Mid-move (any crash point): every original file is complete in the old root, the new root, or both.</summary>
    public void AssertNothingLost(Dictionary<string, string> original)
    {
        foreach (var (file, hash) in original)
        {
            var candidates = new[] { Path.Combine(Old, file), Path.Combine(New, file) }.Where(File.Exists).ToList();
            Assert.True(candidates.Count > 0, file + " is gone");
            if (!FixedUp.Contains(file))
            {
                Assert.Contains(hash, candidates.Select(Hash));
            }
        }
    }

    /// <summary>A deferred move: the old root is exactly as it was and the new root holds no data.</summary>
    public void AssertUntouchedOldRoot(Dictionary<string, string> original, Dictionary<string, string> velopack)
    {
        Assert.Equal(original, DataHashes(Old));
        Assert.Equal(velopack, VelopackHashes());
        Assert.Null(DataRoot.ReadMarker(New));
        Assert.All(DataRoot.Directories, d => Assert.False(Directory.Exists(Path.Combine(New, d)), d + " was left in the new root"));
        Assert.False(Directory.Exists(Path.Combine(New, DataRoot.StagingFolder)));
        Assert.False(File.Exists(Path.Combine(Old, DataRoot.PointerFile)));
        Assert.Equal(Old, DataRoot.Resolve(Old, New));
    }
}

/// <summary>Real file operations, plus: another volume (renames between the two roots fail with ERROR_NOT_SAME_DEVICE), injected failures, copy tampering.</summary>
internal sealed class FakeFs(string oldRoot, string newRoot) : DataRootFileSystem
{
    public bool CrossVolume { get; set; }

    public Func<string, string, Exception?>? OnMove { get; set; }

    public Action<string>? AfterCopy { get; set; }

    public Action<TimeSpan>? OnSleep { get; set; }

    public int Sleeps { get; private set; }

    private static bool Under(string path, string root) =>
        path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) || path.Equals(root, StringComparison.OrdinalIgnoreCase);

    public override void MoveDirectory(string source, string target)
    {
        if (OnMove?.Invoke(source, target) is { } e)
        {
            throw e;
        }

        if (CrossVolume && ((Under(source, oldRoot) && Under(target, newRoot)) || (Under(source, newRoot) && Under(target, oldRoot))))
        {
            throw new IOException("The system cannot move the file to a different disk drive.", unchecked((int)0x80070011));
        }

        base.MoveDirectory(source, target);
    }

    public override void CopyTimestamps(string source, string target)
    {
        base.CopyTimestamps(source, target);
        AfterCopy?.Invoke(target);
    }

    public override void Sleep(TimeSpan delay)
    {
        Sleeps++;
        OnSleep?.Invoke(delay);
    }
}
