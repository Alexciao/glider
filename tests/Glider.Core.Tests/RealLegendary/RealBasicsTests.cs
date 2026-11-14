using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Library;
using Glider.Core.Overlay;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.RealLegendary;

/// <summary>
/// Core's reading side against the real legendary.exe: version and account parsing, the files the exe writes and reads
/// (installed.json, metadata, assets), and the read-only EOS overlay report. Offline, no sign-in, temp config only.
/// </summary>
[Collection(RealLegendaryCollection.Name)]
public sealed class RealBasicsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [RealLegendaryFact]
    public async Task VersionIsThePinnedRelease()
    {
        using var env = new RealLegendaryEnv();
        using var cli = env.Cli();

        var version = await cli.GetVersionAsync(Ct);

        Assert.Equal(LegendaryPin.Version ?? "0.21.1", version);
        Assert.StartsWith("0.21.1", version, StringComparison.Ordinal);
    }

    [RealLegendaryFact]
    public async Task FreshConfigReportsSignedOutThroughTheCliAndWithoutSpawning()
    {
        using var env = new RealLegendaryEnv();
        using var viaCli = env.Cli();
        using var viaFile = env.Cli(o => o.ReadAccountFromUserJson = true);

        Assert.Null(await viaCli.GetAccountOfflineAsync(Ct)); // status --offline --json: "<not logged in>"
        Assert.Null(await viaFile.GetAccountOfflineAsync(Ct)); // no user.json => signed out, no process

        var status = await env.RunAsync("status", "--offline", "--json");
        using var doc = JsonDocument.Parse(status.Stdout);
        Assert.Equal("<not logged in>", doc.RootElement.GetProperty("account").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("games_installed").GetInt32());
        Assert.Equal(env.Config, doc.RootElement.GetProperty("config_directory").GetString()); // LEGENDARY_CONFIG_PATH honoured by the runner
    }

    [RealLegendaryFact]
    public async Task AccountFromAUserJsonIsReadTheSameWayByTheExeAndByCore()
    {
        using var env = new RealLegendaryEnv();
        env.WriteFakeUser();
        using var viaCli = env.Cli();
        using var viaFile = env.Cli(o => o.ReadAccountFromUserJson = true);

        var expected = new LegendaryAccount(RealLegendaryEnv.FakeDisplayName, RealLegendaryEnv.FakeAccountId);

        Assert.Equal(expected, await viaCli.GetAccountOfflineAsync(Ct));
        Assert.Equal(expected, await viaFile.GetAccountOfflineAsync(Ct));
    }

    [RealLegendaryFact]
    public async Task ListInstalledJsonAgreesWithWhatCoreReadsFromInstalledJson()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        using var cli = env.Cli();

        var run = await env.RunAsync("list-installed", "--json");

        Assert.Equal(0, run.ExitCode);
        using var doc = JsonDocument.Parse(run.Stdout);
        var game = Assert.Single(doc.RootElement.EnumerateArray());
        var mine = await cli.GetInstalledAsync(VerifyFixture.App, Ct);
        Assert.NotNull(mine);
        Assert.Equal(game.GetProperty("app_name").GetString(), mine.AppName);
        Assert.Equal(game.GetProperty("version").GetString(), mine.Version);
        Assert.Equal(game.GetProperty("executable").GetString(), mine.Executable);
        Assert.Equal(game.GetProperty("install_size").GetInt64(), mine.InstallSizeBytes);
        Assert.Equal(game.GetProperty("platform").GetString(), mine.Platform);
        Assert.Equal(game.GetProperty("can_run_offline").GetBoolean(), mine.CanRunOffline);
        Assert.Equal(Path.GetFullPath(game.GetProperty("install_path").GetString()!), Path.GetFullPath(mine.InstallPath));
        Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(mine.InstallPath));
        Assert.Equal(1, (await env.RunAsync("status", "--offline", "--json")).Stdout.Split("\"games_installed\": 1").Length - 1);
    }

    [RealLegendaryFact]
    public async Task ListInstalledOutputOfAnEmptyConfigIsAnEmptyList()
    {
        using var env = new RealLegendaryEnv();

        var run = await env.RunAsync("list-installed", "--json");

        Assert.Equal("[]", run.Stdout.Trim());
        Assert.Null(await env.Cli().GetInstalledAsync("Anything", Ct));
    }

    [RealLegendaryFact]
    public async Task InstalledJsonRewrittenByTheExeStillParsesInCore()
    {
        // `move --skip-move` is the one offline command that makes Legendary rewrite installed.json in its own writer format.
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        var elsewhere = Path.Combine(env.Root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        using var cli = env.Cli();

        var move = await cli.MoveAsync(VerifyFixture.App, elsewhere, skipMove: true, null, Ct);

        Assert.True(move.Success, move.Error?.Detail);
        var rewritten = VerifyFixture.ReadInstalled(env)[VerifyFixture.App]!.AsObject();
        Assert.Contains("prereq_info", rewritten); // the exe added every field of its own model
        Assert.Contains("is_preloaded", rewritten);
        var parsed = await cli.GetInstalledAsync(VerifyFixture.App, Ct);
        Assert.NotNull(parsed);
        Assert.Equal(Path.Combine(elsewhere, Path.GetFileName(folder)), parsed.InstallPath);
        Assert.Equal(VerifyFixture.Version, parsed.Version);
        Assert.Equal("Game.exe", parsed.Executable);
        Assert.True(parsed.CanRunOffline);
        Assert.False(parsed.IsDlc);
        Assert.Null(parsed.Prerequisites);
    }

    [RealLegendaryFact]
    public async Task LibraryFilesTheExeAcceptsMapToTheSameGamesInCore()
    {
        // The real catalog items (docs/research/fixtures/legendary/catalog-v0.21.1), in the exact on-disk format, with the exe reading them too.
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        env.Library
            .AddCatalog(LibraryFixture.AlienApp, LibraryFixture.AlienCatalog, "1.0")
            .AddCatalog(LibraryFixture.AlienDlcApp, LibraryFixture.AlienDlcCatalog, "1.0", edit: i => i["mainGameItem"] = Json(
                $$"""{"id": "4106b71233fa4b4ba3b04c818fce91d4", "releaseInfo": [{"appId": "{{LibraryFixture.AlienApp}}"}]}"""))
            .AddCatalog(LibraryFixture.CastlevaniaApp, LibraryFixture.CastlevaniaCatalog, "2.0")
            .AddCatalog(FortniteLayout.AppName, LibraryFixture.FortniteCatalog, "++Fortnite+Release-43.10-CL-1-Windows")
            .Install(LibraryFixture.AlienApp, Path.Combine(env.Games, "Alien"), "0.9", executable: "alien.exe", title: "Alien: Isolation")
            .Save();

        var status = await env.RunAsync("status", "--offline", "--json");
        var installed = await env.RunAsync("list-installed", "--json");
        var library = await new LegendaryLibraryReader(env.Config).ReadAsync(null, Ct);
        var map = GameCatalog.Build(library, CatalogContext.Empty);

        Assert.Equal(0, status.ExitCode);
        Assert.Equal(0, installed.ExitCode);
        using var statusDoc = JsonDocument.Parse(status.Stdout);
        using var installedDoc = JsonDocument.Parse(installed.Stdout);
        // Same installed set, same titles.
        var exeInstalled = installedDoc.RootElement.EnumerateArray().Select(g => g.GetProperty("app_name").GetString()!).Order().ToArray();
        var coreInstalled = map.Values.Where(s => s.InstallStatus == InstallStatus.Installed).Select(s => s.AppName).Order().ToArray();
        Assert.Equal(exeInstalled, coreInstalled);
        Assert.Equal(statusDoc.RootElement.GetProperty("games_installed").GetInt32(), coreInstalled.Length);
        // `games_available` counts the games Legendary lists (no DLC); Core's summaries are games + the DLC rows.
        var available = statusDoc.RootElement.GetProperty("games_available").GetInt32();
        Assert.Equal(available, map.Values.Count(s => s.Kind == GameKind.Game));
        Assert.Equal("Alien: Isolation", map[LibraryFixture.AlienApp].Title);
        Assert.Equal("0.9", map[LibraryFixture.AlienApp].InstalledVersion);
        Assert.True(map[LibraryFixture.AlienApp].UpdateAvailable); // asset build 1.0 != installed 0.9: the same test Legendary applies
        Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(map[VerifyFixture.App].InstallPath!));
    }

    [RealLegendaryFact]
    public async Task EosOverlayInfoIsReadOnlyAndSaysNothingIsManagedOnAFreshConfig()
    {
        using var env = new RealLegendaryEnv();

        var run = await env.RunAsync("eos-overlay", "info");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("No Legendary-managed installation found.", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("Overlay enabled:", run.Stderr, StringComparison.Ordinal);
        Assert.Null(OverlayFiles.ReadInstall(env.Config)); // Core agrees: nothing managed, and `info` wrote no overlay_install.json
        Assert.False(File.Exists(Path.Combine(env.Config, "overlay_install.json")));
        Assert.False(File.Exists(Path.Combine(env.Config, "overlay_version.json")));
    }

    private static System.Text.Json.Nodes.JsonNode Json(string json) => System.Text.Json.Nodes.JsonNode.Parse(json)!;
}
