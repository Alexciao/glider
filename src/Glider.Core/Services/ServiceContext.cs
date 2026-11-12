using Glider.Core.Contracts;

namespace Glider.Core.Services;

/// <summary>Shared collaborators + small helpers for the service-layer components.</summary>
internal sealed class ServiceContext
{
    public const string App = FortniteLayout.AppName;

    public required AppPaths Paths { get; init; }
    public required IHostPlatform Host { get; init; }
    public required ILegendary Legendary { get; init; }
    public required IJobQueue Jobs { get; init; }
    public required IEglService Egl { get; init; }
    public required ISettingsStore SettingsStore { get; init; }
    public required IGameRecordStore Records { get; init; }
    public required ILog Log { get; init; }
    public required TimeProvider Time { get; init; }
    public required StateHub Hub { get; init; }
    public required GliderServiceOptions Options { get; init; }
    public required IArtCache Art { get; init; }
    public required IOnboardingStore Onboarding { get; init; }

    /// <summary>Signals background work to stop at shutdown.</summary>
    public CancellationToken Shutdown { get; init; }

    public Settings Settings => SettingsStore.Current;

    public DateTimeOffset Now => Time.GetUtcNow();

    /// <summary>Jobs that change files (or depend on them being stable).</summary>
    public static bool IsWriteJob(JobKind k) => k is not JobKind.Import;

    /// <summary>Jobs that need a valid Epic session (they download or fetch manifests).</summary>
    public static bool IsNetworkJob(JobKind k) => k is JobKind.Install or JobKind.Update or JobKind.Repair or JobKind.Import;

    public bool IsGameRunning() =>
        Hub.State.RunState != RunState.Idle || Options.ProcessMonitor.Find(FortniteLayout.ShippingProcessName).Count > 0;

    public JobInfo? ActiveJob(params JobKind[] kinds) =>
        Hub.ActiveJobs().FirstOrDefault(j => kinds.Length == 0 || kinds.Contains(j.Kind));

    /// <summary>Throws the right auth error unless signed in. Unknown (pre-warm still running) counts as signed in only if Legendary has a session.</summary>
    public async Task RequireSignedInAsync(CancellationToken ct)
    {
        var auth = Hub.State.Auth.Status;
        if (auth == AuthStatus.Unknown)
        {
            var account = await Legendary.GetAccountOfflineAsync(ct);
            Hub.Mutate(s => s.Auth.Status == AuthStatus.Unknown ? s with { Auth = AuthMapping.From(account) } : s);
            auth = Hub.State.Auth.Status;
        }

        switch (auth)
        {
            case AuthStatus.SignedIn:
                return;
            case AuthStatus.Expired:
                throw new GliderException(Errors.AuthExpired());
            default:
                throw new GliderException(Errors.NotSignedIn());
        }
    }

    /// <summary>In-app notice, plus a Windows toast when the user isn't looking at Glider and notifications are on.</summary>
    public void Notify(NoticeLevel level, string title, string? message, FixAction? action = null, bool toast = true)
    {
        Hub.PublishNotice(new Notice(Guid.NewGuid().ToString("N"), level, title, message, action));
        if (toast && Settings.Notifications && !WindowFocused())
        {
            try
            {
                Host.ShowToast(title, message ?? "");
            }
            catch (Exception e)
            {
                Log.Warn("notify", "toast failed", e);
            }
        }
    }

    private bool WindowFocused()
    {
        try
        {
            return Host.GetWindowInfo().Focused;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Rebuilds our GameRecord from Legendary's installed.json (fast, no process). Keeps Glider-only facts
    /// (adoption, EGL link, install time). Returns the saved record (null if Legendary has no install).
    /// </summary>
    public async Task<GameRecord?> SyncRecordAsync(CancellationToken ct, Func<GameRecord, GameRecord>? adjust = null, bool keepNeedsVerification = true)
    {
        var installed = await Legendary.GetInstalledAsync(App, ct);
        var old = Records.Current;
        if (installed is null)
        {
            return old;
        }

        var rec = new GameRecord(
            installed.InstallPath,
            installed.Version,
            installed.InstallSizeBytes,
            old?.InstalledAt ?? Now,
            old?.AdoptedFromEgl ?? false,
            old?.EglInstallationGuid ?? (string.IsNullOrEmpty(installed.EglGuid) ? null : installed.EglGuid),
            installed.NeedsVerification || (keepNeedsVerification && (old?.NeedsVerification ?? false)));
        if (adjust is not null)
        {
            rec = adjust(rec);
        }

        await SaveRecordAsync(rec);
        return rec;
    }

    public async Task SaveRecordAsync(GameRecord? rec)
    {
        await Records.SaveAsync(rec);
        var broken = rec is not null && !FortniteLayout.LooksLikeInstall(rec.InstallPath);
        Hub.Mutate(s => s with { InstallBroken = broken });
        Hub.Invalidate();
    }

    public EglWriteBack BuildWriteBack(GameRecord rec, LegendaryInstalledGame? installed, LegendaryGameInfo? info) => new(
        App,
        rec.InstallPath,
        rec.Version,
        rec.InstallSizeBytes,
        installed?.InstallTags ?? [],
        Legendary.GetInstalledManifestPath(App, rec.Version),
        info?.CatalogItemId,
        info?.Namespace,
        installed?.Executable ?? info?.LaunchExecutable,
        rec.EglInstallationGuid);

    /// <summary>Background measurement of the install folder (GameState.InstalledSizeBytes). Set by GliderService.</summary>
    public InstalledSizeMeter Sizes { get; set; } = null!;

    /// <summary>Cached game info from the last successful `info` call (set by the update checker).</summary>
    public LegendaryGameInfo? LastGameInfo { get; set; }
}

internal static class AuthMapping
{
    public static AuthState From(LegendaryAccount? a) => a is null
        ? new AuthState(AuthStatus.SignedOut, null)
        : new AuthState(AuthStatus.SignedIn, ToAccount(a));

    /// <summary>Epic has no public avatar endpoint, so the UI renders initials.</summary>
    public static Account ToAccount(LegendaryAccount a) => new(a.AccountId, a.DisplayName, AvatarUrl: null);
}
