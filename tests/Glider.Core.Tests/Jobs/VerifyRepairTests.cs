using System.Security.Cryptography;
using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Tests.Jobs;

/// <summary>
/// A faithful stand-in for Legendary over a real folder: `verify` hashes the files against a manifest and reports counts the way
/// cli.py:1240-1346 does (checked / corrupted / missing, never fixing anything); `repair` re-downloads exactly the bad files.
/// </summary>
internal sealed class FolderBackedLegendary(string installDir, IReadOnlyDictionary<string, byte[]> cdn) : StubLegendary(true)
{
    public List<string> Calls { get; } = [];

    /// <summary>Files the "CDN" can't serve (a repair leaves them bad).</summary>
    public HashSet<string> Unrepairable { get; } = [];

    /// <summary>Report the first verify like Legendary does with no installed manifest (nothing hashed).</summary>
    public bool ManifestMissingOnFirstVerify { get; set; }

    private int _verifies;

    public override Task<LegendaryOutcome> VerifyAsync(string appName, IProgress<LegendaryProgress> progress, CancellationToken ct)
    {
        Calls.Add("verify");
        if (ManifestMissingOnFirstVerify && _verifies++ == 0)
        {
            return Task.FromResult(new LegendaryOutcome(false, Errors.Corrupted("Legendary's copy of the installed manifest is missing."), null, null, new VerifyCounts(0, 0, 0)));
        }

        int corrupted = 0, missing = 0;
        foreach (var (name, content) in cdn)
        {
            var path = Path.Combine(installDir, name);
            if (!File.Exists(path))
            {
                missing++;
            }
            else if (!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(content)))
            {
                corrupted++;
            }
        }

        var counts = new VerifyCounts(cdn.Count, corrupted, missing);
        return Task.FromResult(counts.Bad == 0
            ? new LegendaryOutcome(true, null, null, null, counts)
            : new LegendaryOutcome(false, Errors.Corrupted($"{corrupted} file(s) corrupted, {missing} file(s) missing."), null, null, counts));
    }

    public override Task<LegendaryOutcome> InstallAsync(InstallArgs args, IProgress<LegendaryProgress> progress, CancellationToken ct)
    {
        Calls.Add(args.Mode.ToString().ToLowerInvariant());
        Assert.Equal(InstallMode.Repair, args.Mode);
        foreach (var (name, content) in cdn)
        {
            var path = Path.Combine(installDir, name);
            if (Unrepairable.Contains(name) || (File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(content))))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        return Task.FromResult(new LegendaryOutcome(true, null, null, null));
    }
}

public class VerifyRepairTests
{
    private static (string Dir, Dictionary<string, byte[]> Cdn) Fixture(string root)
    {
        var dir = Path.Combine(root, "Games", "Fortnite");
        var cdn = new Dictionary<string, byte[]>();
        for (var i = 0; i < 10; i++)
        {
            var name = $"FortniteGame/Content/Paks/pakchunk{i}.pak";
            cdn[name] = Enumerable.Range(0, 4096).Select(b => (byte)((b * 31 + i) % 251)).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, name))!);
            File.WriteAllBytes(Path.Combine(dir, name), cdn[name]);
        }

        return (dir, cdn);
    }

    private static async Task<(JobInfo Job, QueueHarness H, FolderBackedLegendary L)> RunAsync(Action<string, Dictionary<string, byte[]>>? damage, Action<FolderBackedLegendary>? configure = null, JobStatus expected = JobStatus.Completed)
    {
        var holder = new TempRoot();
        var (dir, cdn) = Fixture(holder.Path);
        damage?.Invoke(dir, cdn);
        var legendary = new FolderBackedLegendary(dir, cdn);
        configure?.Invoke(legendary);
        var h = new QueueHarness(legendary: legendary);
        var q = await h.StartAsync();
        var job = q.Enqueue(new JobRequest(JobKind.Verify, null, null, null, null));
        var done = await QueueHarness.WaitForStatus(q, job.Id, expected);
        holder.Dispose();
        return (done, h, legendary);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "glider-verify", Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task DeletedAndCorruptedFiles_AreRepaired_ThenConfirmedByASecondVerify()
    {
        var (job, h, legendary) = await RunAsync((dir, _) =>
        {
            File.Delete(Path.Combine(dir, "FortniteGame/Content/Paks/pakchunk2.pak"));
            File.Delete(Path.Combine(dir, "FortniteGame/Content/Paks/pakchunk5.pak"));
            File.WriteAllBytes(Path.Combine(dir, "FortniteGame/Content/Paks/pakchunk7.pak"), [1, 2, 3]);
        });
        await using (h)
        {
            Assert.Equal(["verify", "repair", "verify"], legendary.Calls);
            Assert.Equal(new VerifyReport(FilesChecked: 10, FilesBad: 3, FilesFixed: 3, Verified: true), job.Verify);
            Assert.Null(job.Error);
            Assert.Contains("ok:Verify", h.Hooks.Calls);
        }
    }

    [Fact]
    public async Task AHealthyInstall_IsVerifiedOnce_AndNeverRepaired()
    {
        var (job, h, legendary) = await RunAsync(null);
        await using (h)
        {
            Assert.Equal(["verify"], legendary.Calls);
            Assert.Equal(new VerifyReport(10, 0, 0, true), job.Verify);
        }
    }

    [Fact]
    public async Task FilesTheRepairCouldNotFix_FailTheJobWithTheCounts()
    {
        var (job, h, legendary) = await RunAsync(
            (dir, _) =>
            {
                File.Delete(Path.Combine(dir, "FortniteGame/Content/Paks/pakchunk1.pak"));
                File.Delete(Path.Combine(dir, "FortniteGame/Content/Paks/pakchunk3.pak"));
            },
            l => l.Unrepairable.Add("FortniteGame/Content/Paks/pakchunk3.pak"),
            JobStatus.Failed);
        await using (h)
        {
            Assert.Equal(["verify", "repair", "verify"], legendary.Calls);
            Assert.Equal(ErrorCode.Corrupted, job.Error?.Code);
            Assert.Equal(new VerifyReport(10, 2, 1, false), job.Verify);
            Assert.Contains("failed:Verify:Corrupted", h.Hooks.Calls);
        }
    }

    [Fact]
    public async Task MissingInstalledManifest_StillGetsRepaired()
    {
        // An adopted install whose manifest Legendary can't load: the first verify can't hash anything, the repair (which falls
        // back to the latest manifest) restores the files, and the second verify confirms.
        var (job, h, legendary) = await RunAsync(
            (dir, _) => File.Delete(Path.Combine(dir, "FortniteGame/Content/Paks/pakchunk4.pak")),
            l => l.ManifestMissingOnFirstVerify = true);
        await using (h)
        {
            Assert.Equal(["verify", "repair", "verify"], legendary.Calls);
            Assert.True(job.Verify!.Verified);
            Assert.Equal(10, job.Verify.FilesChecked);
        }
    }

    [Fact]
    public async Task RealLegendaryCli_ReportsFilesCheckedCorruptedAndMissing()
    {
        await using var h = new QueueHarness();
        h.Env.Script("verify", "verify files=12 interval=1 corrupt=1 missing=2");
        var outcome = await h.Cli.VerifyAsync("Fortnite", new Progress<LegendaryProgress>(), TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.Corrupted, outcome.Error?.Code);
        Assert.Equal(new VerifyCounts(12, 1, 2), outcome.Counts);
    }

    [Fact]
    public void Notices_SayWhatWasCheckedAndFixed()
    {
        Assert.Equal(("Verification complete", "Checked 4,996 files. All game files are OK."), JobHooks.VerifyNotice(new VerifyReport(4996, 0, 0, true)));
        var (title, message) = JobHooks.VerifyNotice(new VerifyReport(4996, 3, 3, true));
        Assert.Equal("Repair complete", title);
        Assert.Equal("Checked 4,996 files: 3 damaged or missing, 3 fixed. All game files are OK now.", message);
    }
}
