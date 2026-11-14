// Phase 2 stubs: they become stateful services when their feature lands.
#pragma warning disable CA1822
using Glider.Core.Contracts;

namespace Glider.Core.Overlay;

/// <summary>EOS overlay (Phase 2: `eos-overlay install/update/remove` on the writer lane, enable/disable per user). Not installed for now.</summary>
internal sealed class OverlayService
{
    public static OverlayState NotInstalled { get; } = new(false, false, null, null, false, null, false);

    public OverlayState State() => NotInstalled;

    // Phase 2
    public Task<JobInfo> InstallAsync() => throw new GliderException(Errors.NotAvailableYet("the EOS overlay"));

    // Phase 2
    public Task<OverlayState> SetEnabledAsync(GameFlagRequest request) => throw new GliderException(Errors.NotAvailableYet("the EOS overlay"));

    // Phase 2
    public Task<OverlayState> RemoveAsync() => throw new GliderException(Errors.NotAvailableYet("the EOS overlay"));
}
