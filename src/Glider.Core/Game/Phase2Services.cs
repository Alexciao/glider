// Phase 2 stubs: they become stateful services when their feature lands.
#pragma warning disable CA1822
using Glider.Core.Contracts;

namespace Glider.Core.Game;

/// <summary>Desktop / Start-menu shortcuts per game (Phase 2).</summary>
internal sealed class ShortcutService
{
    // Phase 2
    public Task<ShortcutState> SetAsync(ShortcutRequest request) => throw new GliderException(Errors.NotAvailableYet("shortcuts"));
}
