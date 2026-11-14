using Glider.Core.Art;
using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Infrastructure;
using Glider.Core.Jobs;
using Glider.Core.Library;

namespace Glider.Core.Services;

/// <summary>Shared collaborators + small helpers for the service-layer components. Nothing here is specific to one game.</summary>
internal sealed class ServiceContext
{
    public required AppPaths Paths { get; init; }
    public required IHostPlatform Host { get; init; }
    public required ILegendary Legendary { get; init; }
    public required IJobQueue Jobs { get; init; }
    public required IEglService Egl { get; init; }
    public required ISettingsStore SettingsStore { get; init; }
    public required IGamesStore Games { get; init; }
    public required ILog Log { get; init; }
    public required TimeProvider Time { get; init; }
    public required StateHub Hub { get; init; }
    public required GliderServiceOptions Options { get; init; }
    public required IOnboardingStore Onboarding { get; init; }
    public required MetadataLane Metadata { get; init; }

    /// <summary>Signals background work to stop at shutdown.</summary>
    public CancellationToken Shutdown { get; init; }

    /// <summary>The library (set by GliderService right after construction).</summary>
    public LibraryService Library { get; set; } = null!;

    /// <summary>The lazy art cache behind https://art.glider/ (set by GliderService; also the library's placeholder colours).</summary>
    public ArtCache Art { get; set; } = null!;

    /// <summary>Background measurement of install folders (GameSummary.InstalledSizeBytes). Set by GliderService.</summary>
    public InstalledSizeMeter Sizes { get; set; } = null!;

    public Settings Settings => SettingsStore.Current;

    public DateTimeOffset Now => Time.GetUtcNow();

    /// <summary>Jobs that change files (or depend on them being stable).</summary>
    public static bool IsWriteJob(JobKind k) => k is not JobKind.Import;

    /// <summary>Jobs that need a valid Epic session (they download or fetch manifests).</summary>
    public static bool IsNetworkJob(JobKind k) => k is JobKind.Install or JobKind.Update or JobKind.Repair or JobKind.Import or JobKind.Modify;

    public string TitleOf(string app) => Library.Summary(app)?.Title ?? app;

    /// <summary>A session of the app (or of its base game / one of its DLC) is not idle, or the profile's game process runs.</summary>
    public bool IsGameRunning(string app)
    {
        var s = Hub.State;
        if (s.RunStateOf(app) != RunState.Idle)
        {
            return true;
        }

        var baseApp = Library.Summary(app)?.BaseAppName;
        if (baseApp is not null && s.RunStateOf(baseApp) != RunState.Idle)
        {
            return true;
        }

        var profile = GameProfiles.For(baseApp ?? app);
        return profile.LivenessProcessName is { } name && Options.ProcessMonitor.Find(name).Count > 0;
    }

    /// <summary>Unfinished writer-lane job of the app (optionally of the given kinds).</summary>
    public JobInfo? ActiveJob(string app, params JobKind[] kinds) =>
        Hub.ActiveJobs(app).FirstOrDefault(j => kinds.Length == 0 || kinds.Contains(j.Kind));

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
    public void Notify(NoticeLevel level, string title, string? message, FixAction? action = null, bool toast = true, string? appName = null, string? jobId = null)
    {
        Hub.PublishNotice(new Notice(Guid.NewGuid().ToString("N"), level, title, message, action, appName, jobId));
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
    /// Rebuilds the app's install record from Legendary's installed.json (fast, no process). Keeps Glider-only facts
    /// (adoption, EGL link, install time). Returns the saved record (the old one if Legendary has no install).
    /// </summary>
    public async Task<InstallRecord?> SyncRecordAsync(string app, CancellationToken ct, Func<InstallRecord, InstallRecord>? adjust = null, bool keepNeedsVerification = true)
    {
        var installed = await Legendary.GetInstalledAsync(app, ct);
        var old = Games.Get(app).Install;
        if (installed is null)
        {
            return old;
        }

        var rec = new InstallRecord(
            app,
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

        await SaveRecordAsync(app, rec);
        return rec;
    }

    public async Task SaveRecordAsync(string app, InstallRecord? rec)
    {
        await Games.UpdateAsync(app, e => e with { Install = rec });
        Library.Invalidate(rereadFiles: true);
    }

    public EglWriteBack BuildWriteBack(InstallRecord rec, LegendaryInstalledGame? installed)
    {
        var item = Library.CatalogItem(rec.AppName);
        return new EglWriteBack(
            rec.AppName,
            rec.InstallPath,
            rec.Version,
            rec.InstallSizeBytes,
            installed?.InstallTags ?? [],
            Legendary.GetInstalledManifestPath(rec.AppName, rec.Version),
            item?.CatalogItemId is { Length: > 0 } id ? id : null,
            item?.Namespace is { Length: > 0 } ns ? ns : null,
            installed?.Executable,
            rec.EglInstallationGuid,
            item?.Title ?? installed?.Title);
    }
}

internal static class AuthMapping
{
    public static AuthState From(LegendaryAccount? a) => a is null
        ? new AuthState(AuthStatus.SignedOut, null)
        : new AuthState(AuthStatus.SignedIn, ToAccount(a));

    /// <summary>Epic has no public avatar endpoint, so the UI renders initials.</summary>
    public static Account ToAccount(LegendaryAccount a) => new(a.AccountId, a.DisplayName, AvatarUrl: null);
}

/// <summary>
/// The metadata lane (L§1.8): one online library command at a time (`list`, `list-installed --check-updates`, `info`, update
/// dry runs), never while the writer lane's job is in its pre-download phase (it refreshes the same files then).
/// </summary>
public sealed class MetadataLane : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WriterActivity _writer;

    public MetadataLane(WriterActivity writer) => _writer = writer;

    public bool Busy => _gate.CurrentCount == 0;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WhenNotPreDownloadAsync(ct).ConfigureAwait(false);
            return await work(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
