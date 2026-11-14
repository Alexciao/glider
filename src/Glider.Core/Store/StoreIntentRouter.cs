using Glider.Core.Contracts;
using Glider.Core.Links;

namespace Glider.Core.Store;

/// <summary>What the router needs from the rest of Core (kept as delegates so it is testable without a service graph).</summary>
internal sealed class StoreIntentEnvironment
{
    public required Func<IEnumerable<GameSummary>> Library { get; init; }

    /// <summary>One library refresh, awaited (a just-claimed game may not be there yet).</summary>
    public required Func<CancellationToken, Task> RefreshLibrary { get; init; }

    public required Func<string, CancellationToken, Task> Launch { get; init; }

    /// <summary>Ask the UI to show a route ("game/Fortnite", "store/product/slug", "library", "store").</summary>
    public required Action<string> Navigate { get; init; }

    public required Action<NoticeLevel, string, string?, string?> Notify { get; init; }

    public required Action<string> Log { get; init; }
}

/// <summary>
/// Turns a parsed <see cref="EpicLauncherLink"/> into an action in Glider (docs/research/store-webview.md §2.3). The link's data is
/// untrusted: only the app name, namespace and catalog id are used, to look the game up in the user's own library. Launching needs
/// a trusted gesture; installing never starts a download (the game page's install sheet does); uninstall is always ignored.
/// </summary>
internal sealed class StoreIntentRouter(StoreIntentEnvironment env)
{
    /// <summary>"store/product/&lt;slug&gt;" style route the Store page understands.</summary>
    public static string StoreRoute(string slug, bool bundle) => bundle ? $"store/bundle/{slug}" : $"store/product/{slug}";

    /// <param name="trusted">A user gesture on an Epic origin (or a host-initiated link such as a protocol activation).</param>
    public async Task HandleAsync(EpicLauncherLink link, bool trusted, CancellationToken ct)
    {
        switch (link.Kind)
        {
            case EpicLinkKind.StorePage when link.StoreSlug is { } slug:
                env.Navigate(StoreRoute(slug, link.StoreIsBundle));
                return;
            case EpicLinkKind.StoreHome:
                env.Navigate("store");
                return;
            case EpicLinkKind.Library:
                env.Navigate("library");
                return;
            case EpicLinkKind.Launch or EpicLinkKind.Install or EpicLinkKind.Verify or EpicLinkKind.UpdateCheck:
                await HandleGameAsync(link, trusted, ct).ConfigureAwait(false);
                return;
            default:
                env.Log($"ignored launcher link ({link.Kind})");
                return;
        }
    }

    private async Task HandleGameAsync(EpicLauncherLink link, bool trusted, CancellationToken ct)
    {
        var game = Resolve(link);
        if (game is null)
        {
            try
            {
                await env.RefreshLibrary(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                env.Log($"library refresh for a launcher link failed: {e.GetType().Name}");
            }

            game = Resolve(link);
        }

        if (game is null)
        {
            env.Notify(NoticeLevel.Info, "That game isn't in your library yet", "Refresh your library after claiming it, then try again.", null);
            env.Navigate("library");
            return;
        }

        var route = $"game/{Uri.EscapeDataString(game.AppName)}";
        if (link.Kind == EpicLinkKind.Launch && game.InstallStatus == InstallStatus.Installed && game.Kind != GameKind.Dlc)
        {
            if (trusted)
            {
                await env.Launch(game.AppName, ct).ConfigureAwait(false);
            }
            else
            {
                env.Notify(NoticeLevel.Info, $"The Store asked to open {game.Title}", "Open its page in Glider and press Play to start it.", game.AppName);
                env.Navigate(route);
            }

            return;
        }

        // Not installed (or install/verify/update requested): the game page shows the right button. Nothing starts by itself.
        env.Navigate(route);
    }

    /// <summary>Exact app name first; else the (namespace, catalog item) pair; else null.</summary>
    internal GameSummary? Resolve(EpicLauncherLink link)
    {
        var games = env.Library().ToList();
        if (link.AppName is { } app)
        {
            var byName = games.FirstOrDefault(g => g.AppName.Equals(app, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        if (link.Namespace is { } ns && link.CatalogItemId is { } id)
        {
            return games.FirstOrDefault(g => g.Namespace.Equals(ns, StringComparison.OrdinalIgnoreCase) && g.CatalogItemId.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }
}
