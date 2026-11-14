using System.Diagnostics;
using System.Text;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.RealLegendary;

/// <summary>What the real exe printed for one raw run (for the commands Core has no typed wrapper for).</summary>
internal sealed record RawRun(int ExitCode, string Stdout, string Stderr)
{
    public string All => Stdout + Stderr;
}

/// <summary>
/// A temp Glider data dir whose <c>legendary\</c> folder is the exe's <c>LEGENDARY_CONFIG_PATH</c>. Never the user's real data: no user.json
/// unless a test writes the synthetic one (fake tokens), no network unless a test asks for it.
/// </summary>
internal sealed class RealLegendaryEnv : IDisposable
{
    public const string FakeAccountId = "0123456789abcdef0123456789abcdef";
    public const string FakeDisplayName = "FakeUser";

    private readonly TempDir _dir = new();
    private readonly List<LegendaryCli> _clis = [];

    public RealLegendaryEnv()
    {
        Paths = new AppPaths(_dir.Path, RealLegendary.ExePath ?? throw new InvalidOperationException("real Legendary is not enabled"));
        Paths.EnsureCreated();
        Library = new LibraryFixture(Config) { WritesInstalled = true };
    }

    public AppPaths Paths { get; }

    public TestLog Log { get; } = new();

    public string Root => _dir.Path;

    public string Config => Paths.LegendaryConfig;

    public LibraryFixture Library { get; }

    public string Games => Path.Combine(Root, "Games");

    public static string Exe => RealLegendary.ExePath!;

    public string InstalledJson => Path.Combine(Config, "installed.json");

    /// <summary>
    /// Environment that makes every network call of the exe fail at once ("connection refused" on a closed local port) instead of reaching Epic:
    /// Python's requests honours it. A warm-up or a library sync that a test did not ask for then ends as a plain network failure, and a fake
    /// token is never sent anywhere (Legendary deletes user.json when Epic rejects its token; a refused connection leaves it alone).
    /// </summary>
    public static IReadOnlyDictionary<string, string> NoNetwork { get; } = new Dictionary<string, string>
    {
        ["HTTPS_PROXY"] = "http://127.0.0.1:9",
        ["HTTP_PROXY"] = "http://127.0.0.1:9",
        ["ALL_PROXY"] = "http://127.0.0.1:9",
        ["NO_PROXY"] = "",
    };

    public static LegendaryRunnerOptions OfflineRunner { get; } = new() { ExtraEnvironment = NoNetwork };

    public LegendaryCli Cli(Action<LegendaryOptionsBuilder>? configure = null)
    {
        var b = new LegendaryOptionsBuilder();
        configure?.Invoke(b);
        var cli = new LegendaryCli(Paths, Log, b.Build());
        _clis.Add(cli);
        return cli;
    }

    /// <summary>A user.json that only carries fake values (no real credentials): the real exe accepts it offline, any online call rejects it.</summary>
    public void WriteFakeUser()
    {
        File.WriteAllText(Path.Combine(Config, "user.json"),
            "{\"displayName\": \"" + FakeDisplayName + "\", \"account_id\": \"" + FakeAccountId + "\", \"access_token\": \"FAKE\", \"refresh_token\": \"FAKE\", " +
            "\"expires_at\": \"2000-01-01T00:00:00.000Z\", \"expires_in\": 0, \"refresh_expires_at\": \"2000-01-01T00:00:00.000Z\", \"token_type\": \"bearer\"}");
    }

    /// <summary>Runs the real exe directly (same environment as Glider's runner) and captures everything.</summary>
    public async Task<RawRun> RunAsync(params string[] args) => await RunAsync(args, null, TestContext.Current.CancellationToken);

    public async Task<RawRun> RunAsync(string[] args, string? stdin, CancellationToken ct)
    {
        using var p = StartRaw(args, redirectInput: stdin is not null);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin);
            p.StandardInput.Close();
        }

        await p.WaitForExitAsync(ct);
        return new RawRun(p.ExitCode, await stdout, await stderr);
    }

    /// <summary>Starts the real exe and returns the live process (stdin open when asked): used to hold <c>installed.json.lock</c> from a second process.</summary>
    public Process StartRaw(string[] args, bool redirectInput)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["LEGENDARY_CONFIG_PATH"] = Config;
        psi.Environment["PYTHONUTF8"] = "1";
        foreach (var (k, v) in NoNetwork)
        {
            psi.Environment[k] = v;
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return Process.Start(psi)!;
    }

    public void Dispose()
    {
        foreach (var c in _clis)
        {
            c.Dispose();
        }

        _dir.Dispose();
    }
}

/// <summary>Test-friendly <see cref="LegendaryOptions"/>: no pinned-version shortcut, no user.json shortcut (unless asked), no CDN probe.</summary>
internal sealed class LegendaryOptionsBuilder
{
    public bool ReadAccountFromUserJson { get; set; }

    public string? PinnedVersion { get; set; }

    /// <summary>Lets the exe reach Epic's public endpoints (only the tests marked as network tests).</summary>
    public bool AllowNetwork { get; set; }

    public LegendaryOptions Build() => new()
    {
        Runner = AllowNetwork ? new LegendaryRunnerOptions() : RealLegendaryEnv.OfflineRunner,
        StallTimeout = TimeSpan.FromMinutes(5),
        QuickTimeout = TimeSpan.FromMinutes(1),
        PinnedVersion = PinnedVersion,
        ReadAccountFromUserJson = ReadAccountFromUserJson,
        ProbeCdn = false,
        Fork = false,
    };
}
