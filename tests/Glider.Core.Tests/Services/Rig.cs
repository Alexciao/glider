using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

/// <summary>
/// A GliderService wired to fakes over a temp directory. Legendary's library files (metadata/, assets.json, installed.json)
/// are real files in the temp config dir, written by <see cref="Library"/> and <see cref="FakeLegendary"/>, so the
/// catalog is exercised end to end. The library starts with Fortnite owned at build "v2".
/// </summary>
public sealed class Rig : IDisposable
{
    private GliderService? _service;
    private bool _librarySaved;

    public Rig(IIdleSignal? idle = null, string latest = "v2")
    {
        Root = Path.Combine(Path.GetTempPath(), "glider-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Path.Combine(Root, "data"), Path.Combine(Root, "legendary.exe"));
        Paths.EnsureCreated();
        Host = new FakeHost(Calls);
        Legendary = new FakeLegendary(Calls, Paths.LegendaryConfig);
        Jobs = new FakeJobQueue(Calls);
        Egl = new FakeEgl(Calls);
        Idle = idle;
        Library = LibraryFixture.Fortnite(Paths.LegendaryConfig, latest);
        Library.WritesInstalled = false; // FakeLegendary keeps installed.json
        Processes.Clock = () => Time.GetUtcNow();
    }

    public string Root { get; }
    public CallLog Calls { get; } = new();
    public AppPaths Paths { get; }
    public FakeHost Host { get; }
    public FakeLegendary Legendary { get; }
    public FakeJobQueue Jobs { get; }
    public FakeEgl Egl { get; }
    public FakeSettings Settings { get; } = new();
    public FakeGames Games { get; } = new();
    public FakeLog Log { get; } = new();
    public FakeSink Sink { get; } = new();
    public ManualTime Time { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public FakeServices Services { get; } = new();
    public FakeDisk Disk { get; } = new();
    public FakePublisherCheck Publisher { get; } = new();
    public FakePublisherCheck PrerequisiteCheck { get; } = new();
    public FakeOverlayRegistry OverlayRegistry { get; } = new();
    public FakeLaunchers Launchers { get; } = new();

    /// <summary>Stands in for Program Files (x86): Epic's own overlay folders live under it.</summary>
    public string ProgramFilesX86 => Path.Combine(Root, "pf86");
    public FakeOnboarding Onboarding { get; } = new();
    internal Art.FakeFetcher ArtFetcher { get; } = new();
    public IIdleSignal? Idle { get; }

    /// <summary>The fake shell (shortcuts + registry); set before first use of <see cref="Service"/>. Null = the host offers none.</summary>
    public Glider.Core.Links.IShellIntegration? Shell { get; set; }

    /// <summary>Legendary's library on disk (metadata/assets/entitlements); saved before the service starts.</summary>
    internal LibraryFixture Library { get; }

    /// <summary>Replaces the on-disk size walk (set before first use of <see cref="Service"/>).</summary>
    public Func<string, CancellationToken, long>? Measure { get; set; }

    public TimeSpan SizeMinInterval { get; set; } = TimeSpan.Zero;

    /// <summary>Background session poll (set before first use of <see cref="Service"/>); null = the product default.</summary>
    public TimeSpan? ProcessPollInterval { get; set; }

    /// <summary>Cloud-save commands (set before first use of <see cref="Service"/>; null = none).</summary>
    internal Glider.Core.Saves.ILegendarySaves? SavesLegendary { get; set; }

    internal Glider.Core.Saves.SaveOptions? SaveOptions { get; set; }

    /// <summary>Library delta coalescing window (zero = rebuild at once; with a value, deltas wait for <see cref="Time"/>).</summary>
    public TimeSpan LibraryCoalesce { get; set; } = TimeSpan.Zero;

    /// <summary>Quiet delay before cache/library.json is rewritten (zero = at once; with a value, the write waits for <see cref="Time"/>).</summary>
    public TimeSpan LibraryCacheDelay { get; set; } = TimeSpan.Zero;

    public GliderService Service
    {
        get
        {
            if (_service is null)
            {
                SaveLibrary();
                _service = new GliderService(
                    Paths, Host, Legendary, Jobs, Egl, Settings, Games, Log, Time, Sink,
                    new GliderServiceOptions
                    {
                        ProcessMonitor = Processes,
                        ProcessStarter = Processes,
                        ServiceQuery = Services,
                        DiskInfo = Disk,
                        PublisherCheck = Publisher,
                        PrerequisiteCheck = PrerequisiteCheck,
                        OverlayRegistry = OverlayRegistry,
                        OverlayEnvironment = new Glider.Core.Overlay.OverlayEnvironment(ProgramFilesX86),
                        ThirdPartyLaunchers = Launchers,
                        ThirdPartyHandoffDisplay = TimeSpan.FromSeconds(8),
                        OnboardingStore = Onboarding,
                        ArtFetcher = ArtFetcher,
                        IdleSignal = Idle,
                        Shell = Shell,
                        MeasureDirectory = Measure,
                        SizeMeasureMinInterval = SizeMinInterval,
                        ProcessPollInterval = ProcessPollInterval ?? new GliderServiceOptions().ProcessPollInterval,
                        LibraryCoalesceInterval = LibraryCoalesce,
                        SavesLegendary = SavesLegendary,
                        SaveOptions = SaveOptions,
                        LibraryCacheDelay = LibraryCacheDelay,
                        LibraryRetryDelay = TimeSpan.FromMilliseconds(5),
                        AppVersion = "0.2.0-test",
                        LicensesDirectory = Path.Combine(Root, "licenses"),
                    });
            }

            return _service;
        }
    }

    /// <summary>Writes assets.json/entitlements.json (once; metadata files are written as they are added).</summary>
    public void SaveLibrary()
    {
        if (!_librarySaved)
        {
            _librarySaved = true;
            Library.Save();
            Legendary.WriteInstalledJson();
        }
    }

    /// <summary>Creates a folder that passes the Fortnite profile probe.</summary>
    public string MakeGameFolder(string name = "Fortnite", string? parent = null)
    {
        var game = Path.Combine(parent ?? Root, name);
        var exe = FortniteLayout.ShippingExe(game);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        return game;
    }

    /// <summary>A folder holding a generic game's executable.</summary>
    public string MakeOtherGameFolder(string name, string exe = "Game.exe")
    {
        var game = Path.Combine(Root, name);
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, exe), "MZ");
        return game;
    }

    /// <summary>Fortnite installed at <paramref name="version"/>: installed.json, Glider's record and (optionally) an EGL .item.</summary>
    public InstallRecord Install(string version = "v1", bool sharedWithEgl = false, string? path = null)
    {
        path ??= MakeGameFolder();
        var rec = new InstallRecord(FortniteLayout.AppName, path, version, 100, DateTimeOffset.UnixEpoch, sharedWithEgl, sharedWithEgl ? "guid-1" : null, false);
        Games.Put(FortniteLayout.AppName, Games.Get(FortniteLayout.AppName) with { Install = rec });
        Legendary.Installed = FakeLegendary.MakeInstalled(path, version);
        if (sharedWithEgl)
        {
            Egl.Item = new EglItem("item", "guid-1", path, version, 100, false, false, FortniteLayout.AppName);
        }

        return rec;
    }

    /// <summary>Another owned game (synthetic catalog item, Windows asset at <paramref name="latest"/>), optionally installed.</summary>
    public string AddGame(string app, string title, string latest = "1.0", string? installedVersion = null, string? baseApp = null)
    {
        var item = LibraryFixture.Item(title, "ns-" + app, "item-" + app, baseApp is null ? null : ["addons", "applications"]);
        if (baseApp is not null)
        {
            item["mainGameItem"] = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "item-" + baseApp,
                ["releaseInfo"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["appId"] = baseApp }),
            };
        }

        Library.Add(app, item, latest);
        if (installedVersion is not null)
        {
            var path = baseApp is null ? MakeOtherGameFolder(app) : Legendary.Get(baseApp)?.InstallPath ?? MakeOtherGameFolder(baseApp);
            Legendary.Set(app, FakeLegendary.MakeInstalled(path, installedVersion, app, baseApp is null ? "Game.exe" : "") with { IsDlc = baseApp is not null });
            Games.Put(app, Games.Get(app) with { Install = new InstallRecord(app, path, installedVersion, 100, DateTimeOffset.UnixEpoch, false, null, false) });
        }

        if (_librarySaved)
        {
            Library.Save();
        }

        return app;
    }

    /// <summary>Rebuild the library now (after file changes) and return a game's summary.</summary>
    public async Task<GameSummary?> SummaryAsync(string app = FortniteLayout.AppName)
    {
        await Service.LibraryForTests.ReloadAsync();
        return Service.LibraryForTests.Summary(app);
    }

    /// <summary>Start + wait for pre-warm (and the background sync it starts).</summary>
    public async Task StartWarmAsync()
    {
        await Service.StartAsync();
        await Service.WarmupTask;
        await Service.PrewarmForTests.LibrarySync;
        await Service.LibraryForTests.FlushAsync();
    }

    public void Dispose()
    {
        _service?.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
