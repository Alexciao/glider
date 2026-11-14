using System.Reflection;

namespace Glider.Core.Services;

/// <summary>Optional collaborators and tunables for <see cref="GliderService"/>. Defaults are the real implementations.</summary>
public sealed class GliderServiceOptions
{
    public IProcessMonitor ProcessMonitor { get; init; } = new SystemProcessMonitor();
    public IGameProcessStarter ProcessStarter { get; init; } = new SystemGameProcessStarter();
    public IServiceQuery ServiceQuery { get; init; } = new RegistryServiceQuery();
    public IDiskInfo DiskInfo { get; init; } = new SystemDiskInfo();

    /// <summary>Authenticode check for the EAC setup before it is run elevated.</summary>
    public IPublisherCheck PublisherCheck { get; init; } = new Game.AuthenticodePublisherCheck();

    /// <summary>Authenticode check for a game's prerequisite installer before it is run elevated: any valid signature, any publisher.</summary>
    public IPublisherCheck PrerequisiteCheck { get; init; } = Game.AuthenticodePublisherCheck.AnyValidSignature();

    /// <summary>The per-user EOS overlay registry entries (HKCU only). Tests must always replace it: the default is the real registry.</summary>
    public Overlay.IOverlayRegistry OverlayRegistry { get; init; } = new Overlay.WindowsOverlayRegistry();

    /// <summary>Where Epic's own software keeps an overlay Glider can reuse.</summary>
    public Overlay.OverlayEnvironment OverlayEnvironment { get; init; } = Overlay.OverlayEnvironment.ForMachine();

    /// <summary>The overlay state is re-read (and its update check considered) this often.</summary>
    public TimeSpan OverlayCheckInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>A cached "latest overlay build" older than this triggers a metadata-lane refresh (Legendary refreshes it at most daily).</summary>
    public TimeSpan OverlayVersionMaxAge { get; init; } = TimeSpan.FromHours(25);

    /// <summary>Is Ubisoft Connect / the EA app installed (their URL handler is registered)?</summary>
    public Game.IThirdPartyLaunchers ThirdPartyLaunchers { get; init; } = new Game.RegistryThirdPartyLaunchers();

    /// <summary>
    /// How long a game handed to another store's launcher shows as "Launching" (Untracked) before the UI returns to Play: Glider can't see
    /// when it exits, and a session that never ends would pause downloads for good.
    /// </summary>
    public TimeSpan ThirdPartyHandoffDisplay { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Null = treat the user as idle (auto-update then only waits for the game to close).</summary>
    public IIdleSignal? IdleSignal { get; init; }

    /// <summary>Shortcuts and URL-protocol registration (the host's shell). Null = unavailable (game.setShortcut reports so; protocols are left alone).</summary>
    public Links.IShellIntegration? Shell { get; init; }

    /// <summary>Null = <see cref="Infrastructure.FileOnboardingStore"/> in state/ (loaded by the runtime).</summary>
    public Infrastructure.IOnboardingStore? OnboardingStore { get; init; }

    /// <summary>Null = collections.json in state/ (loaded by the runtime); tests pass an in-memory store.</summary>
    public Infrastructure.CollectionsStore? Collections { get; init; }

    /// <summary>Shared with the writer lane (metadata commands wait for its pre-download phase).</summary>
    public Jobs.WriterActivity? WriterActivity { get; init; }

    /// <summary>Null = sum of file lengths under the folder. Runs on a background thread; must honour the token.</summary>
    public Func<string, CancellationToken, long>? MeasureDirectory { get; init; }

    /// <summary>Installed-size measurements are at least this far apart (they walk ~100 GB of metadata).</summary>
    public TimeSpan SizeMeasureMinInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A cached size for the same folder and version is trusted this long unless a job just changed the files.</summary>
    public TimeSpan SizeCacheMaxAge { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Host wires these from the engine's AutoTune; the fallbacks mirror Legendary's own defaults.</summary>
    public int AutoWorkers { get; init; } = Math.Min(2 * Environment.ProcessorCount, 16);
    public int AutoSharedMemoryMb { get; init; } = 2048;

    public string AppVersion { get; init; } = DefaultAppVersion();

    /// <summary>Folder with license texts (&lt;id&gt;.txt), shipped next to the exe.</summary>
    public string LicensesDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "licenses");

    /// <summary>Minimum spacing of `state` events (≈30/s).</summary>
    public TimeSpan StateCoalesceInterval { get; init; } = TimeSpan.FromMilliseconds(33);

    /// <summary>Process-watch poll period.</summary>
    public TimeSpan ProcessPollInterval { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a launch may take before the game's own process appears.</summary>
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>A session ends when nothing tracked has run for this long (design §2.2: 5 s).</summary>
    public TimeSpan SessionEndGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Library deltas are coalesced over this window (design §1: 50 ms). Zero = rebuild at once (tests).</summary>
    public TimeSpan LibraryCoalesceInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>cache/library.json is rewritten at most this often after changes. Zero = at once (tests).</summary>
    public TimeSpan LibraryCacheDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Delay between parse retries of a Legendary file being rewritten (L§1.8: 3× / 100 ms).</summary>
    public TimeSpan LibraryRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>A full `list -T --json` sync at most this often in the background (design §2: every 30 min).</summary>
    public TimeSpan LibrarySyncInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Library folders / drives in the snapshot are re-queried at most this often.</summary>
    public TimeSpan StorageRefreshInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>No input for this long (and no game running) = idle: Settings.IdleFullSpeed goes Full (design §4).</summary>
    public TimeSpan IdleFullSpeedAfter { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Per-mode process priorities for writer-lane runs (Phase 2 fills in the per-mode plumbing).</summary>
    public Jobs.IResourcePolicy ResourcePolicy { get; init; } = Jobs.DefaultResourcePolicy.Instance;

    /// <summary>Cloud-save tunables (pre-launch timeout, post-exit retries, backup retention). Null = defaults.</summary>
    internal Saves.SaveOptions? SaveOptions { get; init; }

    /// <summary>Cloud-save commands; null = the bundled Legendary when <c>ILegendary</c> is the real CLI (tests inject a fake).</summary>
    internal Saves.ILegendarySaves? SavesLegendary { get; init; }
    /// <summary>Null = download art over HTTP (tests inject a fake).</summary>
    public Art.IArtFetcher? ArtFetcher { get; init; }

    /// <summary>Null = the defaults (6 parallel downloads, 384 MB).</summary>
    public Art.ArtCacheOptions? ArtCache { get; init; }

    /// <summary>The host's image decoder (cover colours, shrinking); null = none.</summary>
    public Art.IImageProcessor? ImageProcessor { get; init; }

    /// <summary>"User idle" threshold for AutoUpdateOnlyWhenIdle when an <see cref="IIdleSignal"/> is present.</summary>
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>An auto-update that had to wait (user active, game running, not signed in) is tried again this often.</summary>
    public TimeSpan AutoUpdateRetryInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Host: serialize a throw-away snapshot + library on the thread pool at start, so the UI's first two requests (which gate the first paint) hit a warm serializer. Off in tests.</summary>
    public bool WarmSerializers { get; init; }

    /// <summary>Fetch Epic's free games in the background (the runtime turns this on; unit tests never touch the network).</summary>
    public bool FreeGamesBackground { get; init; }

    /// <summary>Endpoint, region, HTTP handler and timing of the free-games feed (tests inject a handler).</summary>
    public Store.FreeGamesOptions? FreeGames { get; init; }

    private static string DefaultAppVersion()
    {
        var v = typeof(GliderServiceOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? v[..plus] : v;
    }
}
