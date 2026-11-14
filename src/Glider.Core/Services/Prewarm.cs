using Glider.Core.Auth;
using Glider.Core.Contracts;

namespace Glider.Core.Services;

/// <summary>
/// Background startup work after the first snapshot (served from caches) is out. Order, each step best-effort:
/// legendary version → offline account → Legendary's files → running games → EGL scan + drift → restore jobs →
/// auth warm-up (`list-installed --check-updates --json` through the auth gate on the metadata lane, which also refreshes
/// assets.json for update detection) → Warm → full library sync (`list -T --json`) in the background.
/// </summary>
internal sealed class Prewarm
{
    private readonly ServiceContext _c;
    private readonly AuthService _auth;
    private readonly EglCoordinator _egl;
    private readonly UpdateService _updates;
    private readonly Game.InstallService _install;
    private readonly Game.LaunchService _launch;

    public Prewarm(ServiceContext context, AuthService auth, EglCoordinator egl, UpdateService updates, Game.InstallService install, Game.LaunchService launch)
    {
        _c = context;
        _auth = auth;
        _egl = egl;
        _updates = updates;
        _install = install;
        _launch = launch;
    }

    /// <summary>The background library sync started after warm-up (tests).</summary>
    public Task LibrarySync { get; private set; } = Task.CompletedTask;

    public async Task RunAsync(CancellationToken ct)
    {
        await Step("legendary version", async () =>
        {
            var v = await _c.Legendary.GetVersionAsync(ct);
            _c.Hub.Mutate(s => s with { LegendaryVersion = v });
        });
        await Step("offline account", () => _auth.LoadOfflineAsync(ct));
        await Step("library files", () => _c.Library.ReloadAsync());
        await Step("running games", () =>
        {
            _launch.DetectAllExisting();
            return Task.CompletedTask;
        });
        await Step("egl scan", async () =>
        {
            await _egl.RescanAsync(ct);
            await _egl.ReconcileAllAsync(ct);
        });
        await Step("restore jobs", () => _c.Jobs.RestoreAsync(ct));
        await Step("drift verify", EnqueueDriftVerifiesAsync);
        await Step("session warm-up", () => _auth.WarmUpAsync(ct));

        _c.Hub.Mutate(s => s with { Warm = true });
        _c.Sizes.RequestAll();
        _c.Log.Info("startup", "pre-warm finished");
        _updates.Evaluate();
        if (_c.Hub.State.Auth.Status == AuthStatus.SignedIn && _c.Hub.State.Connectivity != Connectivity.Offline)
        {
            LibrarySync = _c.Library.RefreshInBackground(userInitiated: false);
        }
    }

    private Task EnqueueDriftVerifiesAsync()
    {
        foreach (var app in _egl.TakeVerifyRequests())
        {
            if (_c.ActiveJob(app, JobKind.Verify) is null && _c.Library.Summary(app) is { InstallPath: not null } g)
            {
                _install.EnqueueVerify(g);
            }
        }

        return Task.CompletedTask;
    }

    private async Task Step(string name, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (_c.Shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _c.Log.Warn("startup", $"pre-warm step '{name}' failed", e);
        }
    }
}
