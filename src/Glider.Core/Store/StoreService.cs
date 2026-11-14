// Phase 2 stubs: they become stateful services when their feature lands.
#pragma warning disable CA1822
using Glider.Core.Contracts;

namespace Glider.Core.Store;

/// <summary>
/// The native Store view (Phase 2: a second WebView2 the host creates over the placeholder). Until then every call reports
/// the default, not-created state so the UI can show its fallback.
/// </summary>
internal sealed class StoreService
{
    public static StoreState Default { get; } = new(false, false, false, false, null);

    // Phase 2
    public StoreState Show(StoreShowRequest request) => Default;

    // Phase 2
    public void SetBounds(StoreBounds bounds)
    {
    }

    // Phase 2
    public StoreState Hide() => Default;

    // Phase 2
    public StoreState Back() => Default;

    // Phase 2
    public StoreState Reload() => Default;
}

/// <summary>Epic's weekly free games (Phase 2: freeGamesPromotions fetch + cache). Empty for now.</summary>
internal sealed class FreeGamesService
{
    // Phase 2
    public FreeGamesResult Get() => new([], null);
}
