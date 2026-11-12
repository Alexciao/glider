using System.Text;
using Glider.Core.Egl;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Egl;

/// <summary>Security review: EGL files are shared, writable by other software, and sometimes garbage.</summary>
public class EglBackupClockTests
{
    [Fact]
    public async Task TheBackupJustTakenSurvivesPruningAfterTheClockWasSetBack()
    {
        using var dir = new TempDir();
        var backups = dir.Combine("backups");
        // 25 sets from "the future" (the clock was later set back, e.g. a wrong RTC fixed by NTP).
        for (var i = 0; i < 25; i++)
        {
            Directory.CreateDirectory(Path.Combine(backups, $"29990101-0000{i:00}-000"));
        }

        var target = dir.Combine("Manifests", "X.item");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "old", TestContext.Current.CancellationToken);
        var tx = new EglFileTransaction(backups, new EglServiceOptions { Time = new ManualTimeProvider() }, new TestLog());
        tx.Stage(target, Encoding.UTF8.GetBytes("new"), "Manifests/X.item");
        await tx.CommitAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(tx.BackupDirectory);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(tx.BackupDirectory!, "Manifests", "X.item"), TestContext.Current.CancellationToken));
        Assert.Equal(20, Directory.GetDirectories(backups).Length);
    }
}

public class EglMalformedItemTests
{
    public static TheoryData<string> Bodies => new()
    {
        """{"AppName":["Fortnite"],"InstallLocation":{"x":1},"AppVersionString":12,"InstallSize":"lots","bIsIncompleteInstall":"yes"}""",
        """{"AppName":"Fortnite","InstallLocation":null,"InstallationGuid":7,"InstallTags":"notalist","ExpectingDLCInstalled":[]}""",
        """{"AppName":"Fortnite","InstallLocation":"C:\x","InstallSize":1e400,"AppVersionString":null,"InstallTags":[1,null,{}]}""",
        "[" + new string('[', 5000) + new string(']', 5000) + "]",
        "{\"AppName\":\"Fortnite\",\"a\":" + new string('[', 100_000),
        "\u0000\u0001garbage\uFFFD",
        """{"AppName":"Fortnite","InstallLocation":"\\?\GLOBALROOT\Device\x","InstallSize":-99999999999999999999999}""",
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task ScanNeverThrowsAndKeepsTheRealInstall(string body)
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Games", "Fortnite"));
        t.WriteItem(game);
        File.WriteAllText(Path.Combine(t.Environment.ManifestsDir, "BAD.item"), body);

        using var svc = t.CreateService();
        var state = await svc.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Contains(state.Candidates, c => c.InstallPath == game);
    }

    [Fact]
    public async Task AnOversizedItemIsSkippedWithoutBeingRead()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Games", "Fortnite"));
        t.WriteItem(game);
        await using (var fs = File.Create(Path.Combine(t.Environment.ManifestsDir, "HUGE.item")))
        {
            fs.SetLength(EglService.MaxMetadataBytes + 1); // sparse: must be skipped, never allocated
        }

        using var svc = t.CreateService();
        var state = await svc.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Contains(state.Candidates, c => c.InstallPath == game);
        Assert.Contains(t.Log.Lines, l => l.Contains("never that large", StringComparison.Ordinal));
    }
}
