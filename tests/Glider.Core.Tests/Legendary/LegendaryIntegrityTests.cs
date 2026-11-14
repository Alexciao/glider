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

/// <summary>The fork's pin files stay consistent with the patch they describe (the exe itself is built by .github/workflows/legendary-fork.yml).</summary>
public class LegendaryForkPinTests
{
    private static DirectoryInfo? Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Glider.slnx")))
        {
            dir = dir.Parent;
        }

        return dir;
    }

    [Fact]
    public void PinJsonMatchesThePatchAndTheCandidateManifest()
    {
        var repo = Repo();
        Assert.SkipWhen(repo is null, "not running from the repository");
        using var pin = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repo!.FullName, "tools", "legendary-fork", "PIN.json")));
        var patch = Path.Combine(repo.FullName, pin.RootElement.GetProperty("patch").GetString()!);
        var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(patch).AsSpan().ToArray().Where(b => b != '\r').ToArray()));
        Assert.Equal(pin.RootElement.GetProperty("patchSha256").GetString(), actual); // line endings normalised: git may check the patch out with CRLF
        Assert.Equal(40, pin.RootElement.GetProperty("commit").GetString()!.Length);

        using var candidate = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repo.FullName, "build", "legendary-fork.json")));
        Assert.Equal(pin.RootElement.GetProperty("forkVersion").GetString(), candidate.RootElement.GetProperty("version").GetString());
        Assert.Contains("-glider.", candidate.RootElement.GetProperty("version").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(repo.FullName, "tools", "legendary-fork", "SOURCE-OFFER.txt")));
    }
}
