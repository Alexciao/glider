using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

public class AppSectionConfigTests
{
    private static KeyValuePair<string, string>[] Tags(string v) => [new("install_tags", v)];

    [Fact]
    public void CreatesTheGamesSectionAtTheEndAndTouchesNothingElse()
    {
        const string existing = "[Legendary]\nlocale = de-DE\n\n[Other]\nx = 1\n";
        var text = LegendaryConfigFile.Apply(existing, Tags(",Startup"), "Fortnite")!;
        Assert.Equal("[Legendary]\nlocale = de-DE\n\n[Other]\nx = 1\n\n[Fortnite]\ninstall_tags = ,Startup\n", text);
        Assert.Null(LegendaryConfigFile.Apply(text, Tags(",Startup"), "Fortnite")); // idempotent
    }

    [Fact]
    public void ReplacesOnlyTheGamesKey_KeepingOtherGamesAndKeys()
    {
        const string existing = "[Fortnite]\nstart_params = -d3d12\ninstall_tags = ,Old\n\n[abc123]\ninstall_tags = ,Keep\n";
        var text = LegendaryConfigFile.Apply(existing, Tags(",New,Tags"), "Fortnite")!;
        Assert.Equal("[Fortnite]\nstart_params = -d3d12\ninstall_tags = ,New,Tags\n\n[abc123]\ninstall_tags = ,Keep\n", text);
    }

    [Fact]
    public void GetValueReadsTheRightSectionAndKey()
    {
        const string text = "﻿[Legendary]\r\ninstall_tags = wrong\r\n[Fortnite]\r\nINSTALL_TAGS = ,Startup,FrontEnd\r\n";
        Assert.Equal(",Startup,FrontEnd", LegendaryConfigFile.GetValue(text, "Fortnite", "install_tags"));
        Assert.Null(LegendaryConfigFile.GetValue(text, "Nope", "install_tags"));
        Assert.Null(LegendaryConfigFile.GetValue(null, "Fortnite", "install_tags"));
    }
}

public class InstallTagCliTests
{
    private static LegendaryCli Cli(FakeLegendaryEnv env) => new(env.Paths, env.Log, new LegendaryOptions
    {
        StallTimeout = TimeSpan.FromSeconds(30),
        PinnedVersion = null,
        ReadAccountFromUserJson = false,
    });

    private static string ConfigText(FakeLegendaryEnv env) =>
        File.Exists(Path.Combine(env.Config, "config.ini")) ? File.ReadAllText(Path.Combine(env.Config, "config.ini")) : "";

    [Fact]
    public async Task TagsAreWrittenTheWayLegendaryWritesThem_WhenNothingRuns()
    {
        using var env = new FakeLegendaryEnv();
        using var cli = Cli(env);
        await cli.SetInstallTagsAsync("Fortnite", ["Startup", "FortniteBR"], overwrite: true, TestContext.Current.CancellationToken);

        Assert.Contains("[Fortnite]\ninstall_tags = ,Startup,FortniteBR\n", ConfigText(env).ReplaceLineEndings("\n"));
        Assert.Equal(["", "Startup", "FortniteBR"], await cli.GetConfiguredInstallTagsAsync("Fortnite", TestContext.Current.CancellationToken));
        Assert.Null(await cli.GetConfiguredInstallTagsAsync("Other", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithoutOverwriteAnExistingChoiceIsKept()
    {
        using var env = new FakeLegendaryEnv();
        using var cli = Cli(env);
        await cli.SetInstallTagsAsync("Fortnite", ["Mine"], overwrite: true, TestContext.Current.CancellationToken);
        await cli.SetInstallTagsAsync("Fortnite", ["Other"], overwrite: false, TestContext.Current.CancellationToken);
        Assert.Equal(["", "Mine"], await cli.GetConfiguredInstallTagsAsync("Fortnite", TestContext.Current.CancellationToken));
        await cli.SetInstallTagsAsync("Fortnite", ["Other"], overwrite: true, TestContext.Current.CancellationToken);
        Assert.Equal(["", "Other"], await cli.GetConfiguredInstallTagsAsync("Fortnite", TestContext.Current.CancellationToken));
    }

    /// <summary>Legendary rewrites config.ini at exit and moves a file that changed while it ran aside: never write while one is alive.</summary>
    [Fact]
    public async Task WhileALegendaryProcessRuns_TheValueWaitsForTheNextSpawn()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("verify", "sleep 1500\nerr [cli] INFO: Verification finished successfully.");
        env.Script("uninstall", "uninstalled\nerr [cli] INFO: Game has been uninstalled.");
        using var cli = Cli(env);
        var ct = TestContext.Current.CancellationToken;

        var verify = cli.VerifyAsync("Fortnite", new Progress<LegendaryProgress>(), ct);
        await Task.Delay(500, ct);
        Assert.False(verify.IsCompleted);
        await cli.SetInstallTagsAsync("Fortnite", ["Startup"], overwrite: true, ct);
        Assert.DoesNotContain("install_tags", ConfigText(env));

        await verify;
        Assert.DoesNotContain("install_tags", ConfigText(env)); // nothing ran yet: still pending
        await cli.UninstallAsync("Fortnite", keepFiles: true, ct);
        Assert.Contains("install_tags = ,Startup", ConfigText(env)); // written right before that spawn
    }
}

public class ReimportTests
{
    private static LegendaryCli Cli(FakeLegendaryEnv env) => new(env.Paths, env.Log, new LegendaryOptions { PinnedVersion = null, ReadAccountFromUserJson = false });

    private static void SeedInstalled(FakeLegendaryEnv env, string path, string eglGuid = "")
    {
        var game = new System.Text.Json.Nodes.JsonObject
        {
            ["app_name"] = "Fortnite",
            ["install_path"] = path,
            ["version"] = "v1",
            ["install_size"] = 1,
            ["install_tags"] = new System.Text.Json.Nodes.JsonArray(),
            ["egl_guid"] = eglGuid,
            ["needs_verification"] = false,
            ["title"] = "Fortnite",
            ["executable"] = "x.exe",
            ["can_run_offline"] = false,
            ["requires_ot"] = false,
            ["is_dlc"] = false,
            ["platform"] = "Windows",
        };
        File.WriteAllText(Path.Combine(env.Config, "installed.json"), new System.Text.Json.Nodes.JsonObject { ["Fortnite"] = game }.ToJsonString());
    }

    private static void Scripts(FakeLegendaryEnv env)
    {
        env.Script("uninstall", "uninstalled\nerr [cli] INFO: Game has been uninstalled.");
        env.Script("import", "refuseifinstalled\ninstalled version=v2 path={arg:1}\nerr [cli] INFO: Game \"Fortnite\" has been imported.");
    }

    [Fact]
    public async Task ReimportOfAnInstalledGame_ForgetsItWithKeepFilesThenImports_NoFileTouched()
    {
        using var env = new FakeLegendaryEnv();
        var game = Path.Combine(env.Root, "Games", "Fortnite");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "keep.txt"), "x");
        SeedInstalled(env, game);
        Scripts(env);
        using var cli = Cli(env);

        var outcome = await cli.ImportAsync("Fortnite", game, TestContext.Current.CancellationToken);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        Assert.Equal("v2", (await cli.GetInstalledAsync("Fortnite", TestContext.Current.CancellationToken))!.Version);
        var calls = env.Invocations();
        Assert.Equal(["uninstall", "import"], calls.Select(c => c.Command));
        Assert.Contains("--keep-files", calls[0].Args);
        Assert.Contains("--skip-uninstaller", calls[0].Args);
        Assert.True(File.Exists(Path.Combine(game, "keep.txt")));
    }

    [Fact]
    public async Task ARecordLinkedToEgl_IsNeverForgotten_BecauseLegendaryWouldDeleteEglsOwnManifest()
    {
        using var env = new FakeLegendaryEnv();
        var game = Path.Combine(env.Root, "Games", "Fortnite");
        Directory.CreateDirectory(game);
        SeedInstalled(env, game, eglGuid: "ABCDEF");
        Scripts(env);
        using var cli = Cli(env);

        var outcome = await cli.ImportAsync("Fortnite", game, TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.AlreadyInstalled, outcome.Error!.Code);
        Assert.Empty(env.Invocations());
    }

    [Fact]
    public async Task ImportingFromAnotherFolderWhileInstalledElsewhere_IsRefused()
    {
        using var env = new FakeLegendaryEnv();
        SeedInstalled(env, Path.Combine(env.Root, "A"));
        Scripts(env);
        using var cli = Cli(env);
        var outcome = await cli.ImportAsync("Fortnite", Path.Combine(env.Root, "B"), TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCode.AlreadyInstalled, outcome.Error!.Code);
        Assert.Empty(env.Invocations());
    }
}
