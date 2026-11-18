using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Jobs;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

/// <summary>
/// "Checking files · 122 of 150 GB" for a 37 GB Fortnite: the verify's byte total was installed.json's install_size, which for an
/// imported game is the whole manifest (every optional pack). Legendary's percent is bytes read of the files it verifies (the
/// installed manifest filtered by config.ini's install tags, cli.py:1281-1307), so that is the total now.
/// </summary>
public class VerifyTotalTests
{
    private const long MiB = 1024 * 1024;

    // The owner's Fortnite (read-only from their manifest and config.ini): 734 files, 37,170,389,839 bytes verified; installed.json
    // records 160,618,483,626 (all 1,011 files of the manifest).
    private const long FortniteVerifyBytes = 37_170_389_839;
    private const long FortniteInstallSize = 160_618_483_626;

    private static byte[] Golden => File.ReadAllBytes(Path.Combine(Fixture.Dir, "manifest", "glider-files.manifest"));

    private static JsonElement Expected => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixture.Dir, "manifest", "glider-files.json"))).RootElement;

    [Theory]
    [InlineData("<none>")]
    [InlineData("Startup,Langde,")]
    [InlineData("HighRes")]
    [InlineData(" Langfr , Startup")]
    public void TheManifestGivesTheSameFilesAndBytesAsLegendaryVerify(string configTags)
    {
        // Golden: written and read back by Legendary's own manifest code, totals by cli.py's own filter (tools/bench/make_files_manifest.py).
        var expected = Expected.GetProperty("verify").GetProperty(configTags);
        var total = ManifestReader.TryReadVerifyTotal(Golden, configTags == "<none>" ? null : configTags);

        Assert.NotNull(total);
        Assert.Equal(expected.GetProperty("files").GetInt32(), total.Value.Files);
        Assert.Equal(expected.GetProperty("bytes").GetInt64(), total.Value.Bytes);
    }

    [Fact]
    public void ADamagedManifestHasNoTotal()
    {
        var golden = Golden;
        Assert.Null(ManifestReader.TryReadVerifyTotal([], null));
        Assert.Null(ManifestReader.TryReadVerifyTotal(golden.AsSpan(0, golden.Length / 2), null));
        var damaged = (byte[])golden.Clone();
        damaged[^3] ^= 0xFF;
        Assert.Null(ManifestReader.TryReadVerifyTotal(damaged, null));
    }

    [Fact]
    public void TheCliReadsTheInstalledVersionsManifestAndTheConfigTags()
    {
        using var env = new FakeLegendaryEnv();
        using var cli = new LegendaryCli(env.Paths, env.Log, new LegendaryOptions());
        Assert.Null(cli.VerifyTotal("Game")); // not installed

        File.WriteAllText(Path.Combine(env.Config, "installed.json"),
            """{"Game": {"app_name": "Game", "install_path": "D:\\Games\\Game", "version": "2.0+cl:1", "install_size": 8354173, "platform": "Windows", "install_tags": []}}""");
        Assert.Null(cli.VerifyTotal("Game")); // no manifest of that version

        File.WriteAllText(Path.Combine(env.Config, "manifests", "Game_Windows_1.0.manifest"), "an older build"); // not the installed one
        File.Copy(Path.Combine(Fixture.Dir, "manifest", "glider-files.manifest"), Path.Combine(env.Config, "manifests", "Game_Windows_2.0+cl1.manifest")); // clean_filename drops ':'
        Assert.Equal(new ManifestVerifyTotal(8, 8_354_173), cli.VerifyTotal("Game")); // no config tags: every file

        File.WriteAllText(Path.Combine(env.Config, "config.ini"), "[Legendary]\negl_sync = false\n\n[Game]\ninstall_tags = Startup,Langde,\n");
        Assert.Equal(new ManifestVerifyTotal(6, 4_708_445), cli.VerifyTotal("Game"));
    }

    [Fact]
    public void VerifyBytesAreAShareOfWhatLegendaryReads_NotOfTheInstallRecord()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = new JobProgressTracker("j", "Fortnite", JobSegment.Verify(0, 45), new ProgressBaseline(), time, TimeSpan.Zero, emitted.Add, _ => { },
            installSizeBytes: FortniteInstallSize, verifyTotal: () => new ManifestVerifyTotal(734, FortniteVerifyBytes));
        var parser = new ProgressParser();
        t.OnPhase(JobPhase.Verifying);
        Feed(parser, t, "Verification progress: 609/734 (26.5%) [356.0 MiB/s]\t");
        var v = emitted[^1].Verify!;
        Assert.Equal(FortniteVerifyBytes, v.BytesTotal);
        Assert.Equal((long)(FortniteVerifyBytes * 0.265), v.BytesChecked, tolerance: 1);
        Assert.Equal(609, v.FilesChecked);
        Assert.Equal(734, v.FilesTotal);
    }

    [Fact]
    public void InsideALargeFile_TheKnownTotalPlacesTheBar_FromTheFirstLargeFileOn()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = new JobProgressTracker("j", "Fortnite", JobSegment.Verify(0, 100), new ProgressBaseline(), time, TimeSpan.Zero, emitted.Add, _ => { },
            verifyTotal: () => new ManifestVerifyTotal(734, FortniteVerifyBytes));
        var parser = new ProgressParser();
        t.OnPhase(JobPhase.Verifying);
        Feed(parser, t, "Verification progress: 1/734 (0.0%) [0.0 MiB/s]\t");
        // The first file over 512 MiB: before this change the bar sat at 0% until it was done (no total to place it with).
        const string pak = "=> Verifying large file \"FortniteGame/Content/Paks/pakchunk10-WindowsClient.ucas\": ";
        Feed(parser, t, pak + "50% (4096.0/8192.0 MiB) [350.0 MiB/s]\t");
        var expected = 4096.0 * MiB * 100 / FortniteVerifyBytes;
        Assert.Equal(expected, emitted[^1].PhasePercent, precision: 1);
        Assert.Equal(4096 * MiB, emitted[^1].Verify!.BytesChecked, tolerance: MiB);
        Feed(parser, t, pak + "100% (8192.0/8192.0 MiB) [350.0 MiB/s]\t");
        Feed(parser, t, "Verification progress: 2/734 (23.1%) [350.0 MiB/s]\t"); // Legendary's own figure after the file agrees
        Assert.Equal(23.1, emitted[^1].PhasePercent, precision: 1);
    }

    [Fact]
    public void WithoutATotal_NoBytesAreClaimed_OnlyFiles()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = new JobProgressTracker("j", "Fortnite", JobSegment.Verify(0, 45), new ProgressBaseline(), time, TimeSpan.Zero, emitted.Add, _ => { },
            installSizeBytes: FortniteInstallSize, verifyTotal: () => null);
        var parser = new ProgressParser();
        t.OnPhase(JobPhase.Verifying);
        Feed(parser, t, "Verification progress: 609/734 (26.5%) [356.0 MiB/s]\t");
        var v = emitted[^1].Verify!;
        Assert.Equal(0, v.BytesTotal);
        Assert.Equal(0, v.BytesChecked);
        Assert.Equal(609, v.FilesChecked);
        Assert.Equal(734, v.FilesTotal);
        Assert.Equal(26.5, emitted[^1].PhasePercent);
    }

    private static void Feed(ProgressParser parser, JobProgressTracker tracker, string line)
    {
        Assert.True(parser.TryParse(line, out var published));
        Assert.True(published);
        tracker.Report(parser.Snapshot);
    }
}
