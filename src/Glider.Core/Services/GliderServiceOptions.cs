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

    /// <summary>Null = treat the user as idle (auto-update then only waits for the game to close).</summary>
    public IIdleSignal? IdleSignal { get; init; }

    /// <summary>Null = an <see cref="HttpArtCache"/> over a shared HttpClient.</summary>
    public IArtCache? ArtCache { get; init; }

    /// <summary>Null = <see cref="FileOnboardingStore"/> in state/.</summary>
    public IOnboardingStore? OnboardingStore { get; init; }

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

    /// <summary>How long the launcher stub may run before the Shipping process appears.</summary>
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>A launch skips Legendary's version check if we checked this recently and saw no update.</summary>
    public TimeSpan RecentUpdateCheck { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>"User idle" threshold for AutoUpdateOnlyWhenIdle when an <see cref="IIdleSignal"/> is present.</summary>
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromMinutes(10);

    private static string DefaultAppVersion()
    {
        var v = typeof(GliderServiceOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? v[..plus] : v;
    }
}
