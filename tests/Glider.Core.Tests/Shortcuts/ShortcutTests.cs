using Glider.Core.Contracts;
using Glider.Core.Links;
using Glider.Core.Shortcuts;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Shortcuts;

public class ShortcutNamesTests
{
    [Theory]
    [InlineData("Fortnite", "Fortnite")]
    [InlineData("  Hades:  Battle/Out? ", "Hades Battle Out")]
    [InlineData("What Remains of Edith Finch.", "What Remains of Edith Finch")]
    [InlineData("A<B>C|D\"E*F", "A B C D E F")]
    [InlineData("CON", "app1")]
    [InlineData("nul.txt", "app1")]
    [InlineData("com1", "app1")]
    [InlineData("...", "app1")]
    [InlineData("", "app1")]
    [InlineData(null, "app1")]
    [InlineData("Caf\u00e9 \u30b2\u30fc\u30e0", "Caf\u00e9 \u30b2\u30fc\u30e0")]
    public void FileNamesAreSafe(string? title, string expected) => Assert.Equal(expected, ShortcutNames.FileName(title, "app1"));

    [Fact]
    public void LongTitlesAreTruncated()
    {
        var name = ShortcutNames.FileName(new string('x', 500), "app1");
        Assert.Equal(80, name.Length);
    }

    [Fact]
    public void CandidatesStartWithThePlainTitle()
    {
        var names = ShortcutNames.Candidates("Fortnite", "Fortnite").ToList();
        Assert.Equal("Fortnite.lnk", names[0]);
        Assert.Equal("Fortnite (Glider).lnk", names[1]);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("--launch Fortnite", "Fortnite")]
    [InlineData("--LAUNCH Fortnite ", "Fortnite")]
    [InlineData("--launch ../x", null)]
    [InlineData("--launch Fortnite --evil", null)]
    [InlineData("--launch", null)]
    [InlineData("-applaunch 123", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyGliderArgumentsAreRecognized(string? args, string? app) => Assert.Equal(app, ShortcutNames.AppOf(args));

    [Fact]
    public void ArgumentsRoundTrip() => Assert.Equal("Sugar", ShortcutNames.AppOf(ShortcutNames.LaunchArguments("Sugar")));
}

public class ShortcutServiceTests
{
    private const string Fn = FortniteLayout.AppName;

    private static Rig NewRig(out FakeShell shell)
    {
        var rig = new Rig();
        shell = new FakeShell(rig.Root);
        rig.Shell = shell;
        return rig;
    }

    private static ShortcutRequest Req(ShortcutLocation where, bool create, string app = Fn) => new(app, where, create);

    /// <summary>Alpha: a generic installed game whose installed.json exe is Game.exe.</summary>
    private static async Task<(string Path, string Exe)> InstalledAlpha(Rig rig)
    {
        rig.AddGame("Alpha", "Alpha Quest", "1.0", "1.0");
        var path = rig.Games.Get("Alpha").Install!.InstallPath;
        await rig.StartWarmAsync();
        return (path, Path.Combine(path, "Game.exe"));
    }

    [Fact]
    public async Task CreatesADesktopShortcutThatStartsGliderWithTheGameIcon()
    {
        using var rig = NewRig(out var shell);
        var (_, exe) = await InstalledAlpha(rig);

        var state = await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));

        Assert.Equal(new ShortcutState(true, false), state);
        var link = Assert.Single(shell.FakeLinks.Files);
        Assert.Equal(shell.DesktopLink("Alpha Quest"), link.Key);
        Assert.Equal(shell.LaunchExePath, link.Value.Target);
        Assert.Equal("--launch Alpha", link.Value.Arguments);
        Assert.Equal(exe, link.Value.IconPath);
        Assert.Equal(Path.GetDirectoryName(shell.LaunchExePath), link.Value.WorkingDirectory);
        Assert.Equal("Play Alpha Quest", link.Value.Description);
        Assert.Equal(state, rig.Games.Get("Alpha").Shortcuts); // kept in games.json
    }

    [Fact]
    public async Task StartMenuAndDesktopAreIndependent()
    {
        using var rig = NewRig(out var shell);
        await InstalledAlpha(rig);

        await rig.Service.SetShortcut(Req(ShortcutLocation.StartMenu, true, "Alpha"));
        var both = await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));
        Assert.Equal(new ShortcutState(true, true), both);
        Assert.True(shell.FakeLinks.Exists(shell.StartLink("Alpha Quest")));

        var onlyStart = await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, false, "Alpha"));
        Assert.Equal(new ShortcutState(false, true), onlyStart);
        Assert.False(shell.FakeLinks.Exists(shell.DesktopLink("Alpha Quest")));
        Assert.True(shell.FakeLinks.Exists(shell.StartLink("Alpha Quest")));
        Assert.Equal(onlyStart, rig.Games.Get("Alpha").Shortcuts);
    }

    [Fact]
    public async Task FallsBackToGliderIconWhenTheGameExeIsMissing()
    {
        using var rig = NewRig(out var shell);
        var (_, exe) = await InstalledAlpha(rig);
        File.Delete(exe);

        await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));

        Assert.Equal(shell.LaunchExePath, shell.FakeLinks.Files.Values.Single().IconPath);
    }

    [Fact]
    public async Task CreatingTwiceRewritesTheSameFile()
    {
        using var rig = NewRig(out var shell);
        await InstalledAlpha(rig);

        await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));
        await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));

        Assert.Single(shell.FakeLinks.Files);
    }

    [Fact]
    public async Task NeverOverwritesOrDeletesAShortcutThatIsNotOurs()
    {
        using var rig = NewRig(out var shell);
        await InstalledAlpha(rig);
        // Epic's own launcher shortcut has the same name.
        var eglLink = new ShellLinkSpec("C:\\Epic\\EpicGamesLauncher.exe", "com.epicgames.launcher://apps/x?action=launch", "", "", 0, "");
        shell.FakeLinks.Put(shell.DesktopLink("Alpha Quest"), eglLink);

        var state = await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));

        Assert.True(state.Desktop);
        Assert.Equal(eglLink, shell.FakeLinks.Read(shell.DesktopLink("Alpha Quest")));
        Assert.Equal("--launch Alpha", shell.FakeLinks.Read(shell.DesktopLink("Alpha Quest (Glider)"))!.Arguments);

        await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, false, "Alpha"));

        Assert.Equal(eglLink, shell.FakeLinks.Read(shell.DesktopLink("Alpha Quest")));
        Assert.False(shell.FakeLinks.Exists(shell.DesktopLink("Alpha Quest (Glider)")));
    }

    [Fact]
    public async Task RefusesWhatCannotHaveAShortcut()
    {
        using var rig = NewRig(out _);
        await InstalledAlpha(rig);
        rig.AddGame("Beta", "Beta", "1.0"); // owned, not installed
        rig.AddGame("AlphaDlc", "Alpha DLC", "1.0", baseApp: "Alpha");
        await rig.Service.LibraryForTests.ReloadAsync();

        Assert.Equal(ErrorCode.NotInstalled, (await Assert.ThrowsAsync<GliderException>(() => rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Beta")))).Error.Code);
        Assert.Equal(ErrorCode.InvalidRequest, (await Assert.ThrowsAsync<GliderException>(() => rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "AlphaDlc")))).Error.Code);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<GliderException>(() => rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Nope")))).Error.Code);
        Assert.Equal(ErrorCode.InvalidRequest, (await Assert.ThrowsAsync<GliderException>(() => rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "../x")))).Error.Code);
        Assert.Equal(ErrorCode.InvalidRequest, (await Assert.ThrowsAsync<GliderException>(() => rig.Service.SetShortcut(Req((ShortcutLocation)9, true, "Alpha")))).Error.Code);
    }

    [Fact]
    public async Task RemovingAShortcutForAnUninstalledGameIsAllowed()
    {
        using var rig = NewRig(out _);
        rig.AddGame("Beta", "Beta", "1.0");
        await rig.StartWarmAsync();

        var state = await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, false, "Beta"));

        Assert.Equal(new ShortcutState(false, false), state);
    }

    [Fact]
    public async Task WithoutAShellTheFeatureIsReportedUnavailable()
    {
        using var rig = new Rig();
        await InstalledAlpha(rig);

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha")));
        Assert.Contains("aren't available", e.Error.Detail ?? e.Error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- repair

    [Fact]
    public async Task AMovedGameGetsItsIconRepaired()
    {
        using var rig = NewRig(out var shell);
        var (_, oldExe) = await InstalledAlpha(rig);
        await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));
        await rig.Service.SetShortcut(Req(ShortcutLocation.StartMenu, true, "Alpha"));

        var moved = Path.Combine(rig.Root, "Moved", "Alpha");
        Directory.CreateDirectory(moved);
        File.WriteAllText(Path.Combine(moved, "Game.exe"), "MZ");
        rig.Games.Put("Alpha", rig.Games.Get("Alpha") with { Install = rig.Games.Get("Alpha").Install! with { InstallPath = moved } });
        rig.Legendary.Set("Alpha", Glider.Core.Tests.Services.FakeLegendary.MakeInstalled(moved, "1.0", "Alpha"));
        await rig.Service.LibraryForTests.ReloadAsync();

        await rig.Service.ShortcutsForTests.ReconcileAllAsync();

        Assert.Equal(Path.Combine(moved, "Game.exe"), shell.FakeLinks.Read(shell.DesktopLink("Alpha Quest"))!.IconPath);
        Assert.Equal(Path.Combine(moved, "Game.exe"), shell.FakeLinks.Read(shell.StartLink("Alpha Quest"))!.IconPath);
        Assert.NotEqual(oldExe, shell.FakeLinks.Read(shell.DesktopLink("Alpha Quest"))!.IconPath);
        Assert.Equal(new ShortcutState(true, true), rig.Games.Get("Alpha").Shortcuts);
    }

    [Fact]
    public async Task AnUninstalledGameLosesItsShortcuts()
    {
        using var rig = NewRig(out var shell);
        await InstalledAlpha(rig);
        await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));
        await rig.Service.SetShortcut(Req(ShortcutLocation.StartMenu, true, "Alpha"));

        rig.Games.Put("Alpha", rig.Games.Get("Alpha") with { Install = null });
        rig.Legendary.Set("Alpha", null);
        await rig.Service.LibraryForTests.ReloadAsync();
        await rig.Service.ShortcutsForTests.ReconcileAllAsync();

        Assert.Empty(shell.FakeLinks.Files);
        Assert.Equal(new ShortcutState(false, false), rig.Games.Get("Alpha").Shortcuts);
    }

    [Fact]
    public async Task AShortcutTheUserDeletedStaysDeleted()
    {
        using var rig = NewRig(out var shell);
        await InstalledAlpha(rig);
        await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));
        await rig.Service.SetShortcut(Req(ShortcutLocation.StartMenu, true, "Alpha"));
        shell.FakeLinks.Delete(shell.DesktopLink("Alpha Quest"));
        // a change that triggers a re-check: the folder moves
        var moved = Path.Combine(rig.Root, "Moved", "Alpha");
        Directory.CreateDirectory(moved);
        File.WriteAllText(Path.Combine(moved, "Game.exe"), "MZ");
        rig.Games.Put("Alpha", rig.Games.Get("Alpha") with { Install = rig.Games.Get("Alpha").Install! with { InstallPath = moved } });

        await rig.Service.ShortcutsForTests.ReconcileAllAsync();

        Assert.False(shell.FakeLinks.Exists(shell.DesktopLink("Alpha Quest")));
        Assert.Equal(new ShortcutState(false, true), rig.Games.Get("Alpha").Shortcuts);
        Assert.True(shell.FakeLinks.Exists(shell.StartLink("Alpha Quest")));
    }

    [Fact]
    public async Task NothingIsRemovedOnAGuessBeforeTheLibraryIsKnown()
    {
        using var rig = NewRig(out var shell);
        var path = shell.DesktopLink("Alpha Quest");
        shell.FakeLinks.Put(path, new ShellLinkSpec(shell.LaunchExePath, "--launch Alpha", "", "", 0, ""));
        // games.json says there is a shortcut but no install record, and the service has not read the library yet
        rig.Games.Put("Alpha", GameEntry.Empty with { Shortcuts = new ShortcutState(true, false) });

        await rig.Service.ShortcutsForTests.ReconcileAllAsync();

        Assert.True(shell.FakeLinks.Exists(path));
        Assert.Equal(new ShortcutState(true, false), rig.Games.Get("Alpha").Shortcuts);
    }

    [Fact]
    public async Task UnchangedGamesAreNotRewrittenOnEveryRecordChange()
    {
        using var rig = NewRig(out var shell);
        await InstalledAlpha(rig);
        await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));
        var writes = shell.FakeLinks.Writes;

        rig.Games.Put("Alpha", rig.Games.Get("Alpha") with { PlaytimeSeconds = 99 }); // e.g. the 30 s playtime flush
        await rig.Service.ShortcutsForTests.ReconcileAllAsync();

        Assert.Equal(writes, shell.FakeLinks.Writes);
    }

    [Fact]
    public async Task RecordChangesTriggerARepairAfterTheDebounce()
    {
        using var rig = NewRig(out var shell);
        await InstalledAlpha(rig);
        await rig.Service.SetShortcut(Req(ShortcutLocation.Desktop, true, "Alpha"));

        rig.Games.Put("Alpha", rig.Games.Get("Alpha") with { Install = null });
        rig.Legendary.Set("Alpha", null);
        await rig.Service.LibraryForTests.ReloadAsync();
        rig.Games.Put("Alpha", rig.Games.Get("Alpha") with { PlaytimeSeconds = 1 }); // Changed → debounce timer
        rig.Time.Advance(TimeSpan.FromSeconds(2));

        for (var i = 0; i < 200 && shell.FakeLinks.Files.Count > 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Empty(shell.FakeLinks.Files);
    }

    // ---------------------------------------------------------------- uninstalling Glider itself

    [Fact]
    public void UninstallingGliderRemovesOnlyItsOwnShortcuts()
    {
        var links = new FakeShellLinks();
        var exe = "C:\\Glider\\Glider.exe";
        links.Put("D\\a.lnk", new ShellLinkSpec(exe, "--launch Fortnite", "", "", 0, ""));
        links.Put("D\\b.lnk", new ShellLinkSpec("C:\\Other\\glider.exe", "--launch Sugar", "", "", 0, "")); // same exe name: ours (stub vs current\)
        links.Put("D\\steam.lnk", new ShellLinkSpec("C:\\Steam\\steam.exe", "--launch Sugar", "", "", 0, "")); // someone else's --launch
        links.Put("D\\egl.lnk", new ShellLinkSpec("C:\\Epic\\EpicGamesLauncher.exe", "com.epicgames.launcher://x", "", "", 0, ""));
        links.Put("D\\plain.lnk", new ShellLinkSpec(exe, "", "", "", 0, "")); // Glider's own start-menu entry
        links.Put("S\\c.lnk", new ShellLinkSpec(exe, "--launch ../evil", "", "", 0, ""));
        links.Put("S\\d.lnk", new ShellLinkSpec(exe, "--launch Foo", "", "", 0, ""));

        var removed = ShortcutCleanup.RemoveAllOurs(links, exe, "D", null, "S");

        Assert.Equal(3, removed);
        Assert.Equal(["D\\egl.lnk", "D\\plain.lnk", "D\\steam.lnk", "S\\c.lnk"], links.Files.Keys.Order().ToArray());
    }
}
