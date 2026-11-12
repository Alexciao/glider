using System.Security.Cryptography;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

/// <summary>Security review: a damaged or swapped legendary.exe is reported as "reinstall Glider", never run.</summary>
public class LegendaryIntegrityTests
{
    private static LegendaryCommand Status => new(LegendaryCommandKind.Status, ["status", "--offline", "--json"]) { CaptureStdout = true };

    [Fact]
    public async Task AMismatchingExeIsNotRun()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("status", "out {}");
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log, options: new LegendaryRunnerOptions { ExpectedSha256 = new string('0', 64) });

        var e = await Assert.ThrowsAsync<GliderException>(() => runner.RunAsync(Status, null, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.LegendaryMissing, e.Error.Code);
        Assert.Empty(env.Invocations());
    }

    [Fact]
    public async Task TheExpectedExeRuns()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("status", "out {}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(env.Paths.LegendaryExe)));
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log, options: new LegendaryRunnerOptions { ExpectedSha256 = hash });

        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(0, (await runner.RunAsync(Status, null, ct)).ExitCode);
        Assert.Equal(0, (await runner.RunAsync(Status, null, ct)).ExitCode); // cached: hashed once
        Assert.Equal(2, env.Invocations().Count);
    }

    [Fact]
    public void ThePinMatchesTheBuildManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glider.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.SkipWhen(dir is null, "not running from the repository");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir!.FullName, "build", "legendary.json")));
        Assert.Equal(LegendaryCli.PinnedSha256, doc.RootElement.GetProperty("sha256").GetString());
    }
}
