using Glider.Core.Contracts;
using Glider.Core.Game;

namespace Glider.Core.Services;

/// <summary>
/// "Ready to uninstall the Epic Games Launcher?": everything that would still depend on EGL once it is gone. Blockers are things
/// that stop working (games only EGL has registered, EGL mid-operation); warnings are things that need a decision or a one-time
/// step (Epic links, the overlay, cloud saves never synced through Glider, an anti-cheat that was never started by Glider, an update
/// EGL left half-done). Nothing here modifies anything.
/// </summary>
internal sealed class EglReadinessChecker
{
    private readonly ServiceContext _c;

    public EglReadinessChecker(ServiceContext context) => _c = context;

    public EglReadiness Check(EglState state)
    {
        var items = new List<ReadinessItem>();
        if (!state.LauncherInstalled && state.Installs.Count == 0)
        {
            return new EglReadiness(true, [new ReadinessItem(null, "Epic Games Launcher", ReadinessSeverity.Ok, "The Epic Games Launcher isn't installed on this PC.")]);
        }

        var pending = SafePending();
        var summaries = _c.Library.Games;
        foreach (var i in state.Installs)
        {
            var summary = summaries.GetValueOrDefault(i.AppName);
            switch (i.Status)
            {
                case EglInstallStatus.Importable:
                    items.Add(new ReadinessItem(i.AppName, i.Title, ReadinessSeverity.Blocker,
                        "Only the Epic Games Launcher has this game registered. Import it into Glider first, or it can't be updated or launched here.", ReadinessFix.Import));
                    break;
                case EglInstallStatus.Busy:
                    items.Add(new ReadinessItem(i.AppName, i.Title, ReadinessSeverity.Blocker,
                        i.Reason ?? "The Epic Games Launcher is still working on it. Let it finish, or close it.", ReadinessFix.WaitForEgl));
                    break;
                case EglInstallStatus.Stale:
                    items.Add(new ReadinessItem(i.AppName, i.Title, ReadinessSeverity.Warning,
                        i.Reason ?? "The Epic Games Launcher lists files that are gone. Nothing depends on that entry.", ReadinessFix.None));
                    break;
                case EglInstallStatus.NotOwned:
                    items.Add(new ReadinessItem(i.AppName, i.Title, ReadinessSeverity.Warning,
                        "The Epic Games Launcher lists this game but it isn't on the signed-in account, so Glider can't take it over.", ReadinessFix.None));
                    break;
                case EglInstallStatus.Imported:
                    if (HasUnfinishedEglUpdate(i, pending))
                    {
                        items.Add(new ReadinessItem(i.AppName, i.Title, ReadinessSeverity.Warning,
                            "The Epic Games Launcher started an update for this game and didn't finish it. Glider will offer the update itself.", ReadinessFix.None));
                    }

                    break;
            }
        }

        foreach (var g in summaries.Values.Where(g => g.Kind == GameKind.Game && g.InstallStatus is InstallStatus.Installed or InstallStatus.Broken))
        {
            if (g.ThirdParty is ThirdPartyStore.Ea or ThirdPartyStore.Ubisoft)
            {
                items.Add(new ReadinessItem(g.AppName, g.Title, ReadinessSeverity.Warning,
                    $"Starts through {g.ThirdPartyName ?? "another store's launcher"}, which has to be installed and signed in on its own. The Epic Games Launcher isn't involved.", ReadinessFix.Launch));
            }

            if (g.InstallPath is { } path && GameProfiles.DetectAntiCheat(g.AppName, path) == AntiCheat.EasyAntiCheat && _c.Games.Get(g.AppName).LastPlayedAt is null)
            {
                items.Add(new ReadinessItem(g.AppName, g.Title, ReadinessSeverity.Warning,
                    "Uses Easy Anti-Cheat, set up so far by the Epic Games Launcher or the game. Play it once in Glider so its anti-cheat service is checked while EGL is still there.", ReadinessFix.Launch));
            }

            if (g.CloudSaves && g.SaveStatus == SaveSyncStatus.Unknown && _c.Settings.CloudSaves)
            {
                items.Add(new ReadinessItem(g.AppName, g.Title, ReadinessSeverity.Warning,
                    "Cloud saves have never been synced through Glider. Sync once before removing the Epic Games Launcher, so the newest save is the one you keep.", ReadinessFix.SyncSaves));
            }
        }

        if (!_c.Settings.HandleEpicLinks)
        {
            items.Add(new ReadinessItem(null, "Epic links", ReadinessSeverity.Warning,
                "Store buttons and old Epic shortcuts (com.epicgames.launcher://) still open the Epic Games Launcher. Turn on \"Open Epic links in Glider\" to take them over.", ReadinessFix.TurnOnEpicLinks));
        }

        if (!_c.Hub.BuildSnapshot().Overlay.Installed && EglOverlayPresent())
        {
            items.Add(new ReadinessItem(null, "Epic overlay", ReadinessSeverity.Warning,
                "The in-game Epic overlay comes from the Epic Games Launcher and would go with it. Install Glider's overlay to keep it.", ReadinessFix.InstallOverlay));
        }

        if (items.Count == 0)
        {
            items.Add(new ReadinessItem(null, "Epic Games Launcher", ReadinessSeverity.Ok,
                "Nothing depends on the Epic Games Launcher anymore. You can uninstall it; your games stay where they are."));
        }

        var ordered = items
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.AppName is null ? 1 : 0)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new EglReadiness(ordered.All(i => i.Severity != ReadinessSeverity.Blocker), ordered);
    }

    private IReadOnlyList<EglPendingUpdate> SafePending()
    {
        try
        {
            return _c.Egl.PendingUpdates();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _c.Log.Warn("egl", "couldn't read EGL's pending updates", e);
            return [];
        }
    }

    private bool HasUnfinishedEglUpdate(EglInstall install, IReadOnlyList<EglPendingUpdate> pending)
    {
        if (pending.Any(p => string.Equals(p.AppName, install.AppName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            return _c.Egl.InspectForAdoption(install.InstallPath, install.AppName) is { StagingPresent: true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool EglOverlayPresent()
    {
        try
        {
            return _c.Options.EglOverlayProbe();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
