using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

/// <summary>A GliderService wired to fakes over a temp directory.</summary>
public sealed class Rig : IDisposable
{
    private GliderService? _service;

    public Rig(IIdleSignal? idle = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "glider-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Path.Combine(Root, "data"), Path.Combine(Root, "legendary.exe"));
        Host = new FakeHost(Calls);
        Legendary = new FakeLegendary(Calls);
        Jobs = new FakeJobQueue(Calls);
        Egl = new FakeEgl(Calls);
        Idle = idle;
    }

    public string Root { get; }
    public CallLog Calls { get; } = new();
    public AppPaths Paths { get; }
    public FakeHost Host { get; }
    public FakeLegendary Legendary { get; }
    public FakeJobQueue Jobs { get; }
    public FakeEgl Egl { get; }
    public FakeSettings Settings { get; } = new();
    public FakeRecords Records { get; } = new();
    public FakeLog Log { get; } = new();
    public FakeSink Sink { get; } = new();
    public ManualTime Time { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public FakeServices Services { get; } = new();
    public FakeDisk Disk { get; } = new();
    public FakePublisherCheck Publisher { get; } = new();
    public FakeArt Art { get; } = new();
    public FakeOnboarding Onboarding { get; } = new();
    public IIdleSignal? Idle { get; }

    /// <summary>Replaces the on-disk size walk (set before first use of <see cref="Service"/>).</summary>
    public Func<string, CancellationToken, long>? Measure { get; set; }

    public TimeSpan SizeMinInterval { get; set; } = TimeSpan.Zero;

    public GliderService Service => _service ??= new GliderService(
        Paths, Host, Legendary, Jobs, Egl, Settings, Records, Log, Time, Sink,
        new GliderServiceOptions
        {
            ProcessMonitor = Processes,
            ProcessStarter = Processes,
            ServiceQuery = Services,
            DiskInfo = Disk,
            PublisherCheck = Publisher,
            ArtCache = Art,
            OnboardingStore = Onboarding,
            IdleSignal = Idle,
            MeasureDirectory = Measure,
            SizeMeasureMinInterval = SizeMinInterval,
            AppVersion = "0.1.0-test",
            LicensesDirectory = Path.Combine(Root, "licenses"),
        });

    /// <summary>Creates a folder that passes FortniteLayout.LooksLikeInstall.</summary>
    public string MakeGameFolder(string name = "Fortnite", string? parent = null)
    {
        var game = Path.Combine(parent ?? Root, name);
        var exe = FortniteLayout.ShippingExe(game);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        return game;
    }

    public GameRecord Install(string version = "v1", bool sharedWithEgl = false, string? path = null)
    {
        path ??= MakeGameFolder();
        var rec = new GameRecord(path, version, 100, DateTimeOffset.UnixEpoch, sharedWithEgl, sharedWithEgl ? "guid-1" : null, false);
        Records.Current = rec;
        Legendary.Installed = FakeLegendary.MakeInstalled(path, version);
        if (sharedWithEgl)
        {
            Egl.Item = new EglInstall("item", "guid-1", path, version, 100, false, false);
        }

        return rec;
    }

    /// <summary>Start + wait for pre-warm.</summary>
    public async Task StartWarmAsync()
    {
        await Service.StartAsync();
        await Service.WarmupTask;
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
    }
}
