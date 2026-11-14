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

/// <summary>Ubisoft Connect / EA app linking via `legendary activate` (Phase 2, metadata lane).</summary>
internal sealed class ActivationService
{
    // Phase 2
    public Task ActivateAsync(string app) => throw new GliderException(Errors.NotAvailableYet("third-party activation"));
}

/// <summary>Running a game's declared prerequisites elevated and remembering them in state/prereqs.json (Phase 2).</summary>
internal sealed class PrerequisiteService
{
    // Phase 2
    public Task InstallAsync(string app) => throw new GliderException(Errors.NotAvailableYet("prerequisites"));
}
