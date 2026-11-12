using Glider.Core.Auth;
using Glider.Core.Contracts;

namespace Glider.Core.Services;

/// <summary>
/// Background startup work after the first snapshot is out. Order (each step is best-effort):
/// legendary version → offline account → EGL scan + drift → token refresh → game info → restore jobs → Warm.
/// </summary>
internal sealed class Prewarm
{
    private readonly ServiceContext _c;
    private readonly AuthService _auth;
    private readonly UpdateChecker _updates;
    private bool _verifyAfterRestore;

    public Prewarm(ServiceContext context, AuthService auth, UpdateChecker updates)
    {
        _c = context;
        _auth = auth;
        _updates = updates;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await Step("legendary version", async () =>
        {
            var v = await _c.Legendary.GetVersionAsync(ct);
            _c.Hub.Mutate(s => s with { LegendaryVersion = v });
        });
        await Step("offline account", () => _auth.LoadOfflineAsync(ct));
        await Step("egl scan", () => ScanAndReconcileAsync(ct));
        await Step("session refresh", () => _auth.RefreshAsync(ct));
        await Step("game info", () => _updates.CheckAsync(userInitiated: false, ct));
        await Step("restore jobs", () => _c.Jobs.RestoreAsync(ct));
        await Step("drift verify", EnqueueDriftVerifyAsync);

        _c.Hub.Mutate(s => s with { Warm = true });
        _c.Sizes.Request(filesChanged: false);
        _c.Log.Info("startup", "pre-warm finished");
        _updates.Evaluate();
    }

    private Task EnqueueDriftVerifyAsync()
    {
        if (_verifyAfterRestore && _c.Records.Current is { } rec && _c.ActiveJob(JobKind.Verify) is null)
        {
            _c.Jobs.Enqueue(new JobRequest(JobKind.Verify, null, null, rec.Version, rec.Version));
        }

        _verifyAfterRestore = false;
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

    /// <summary>EGL scan, then reconcile our record with EGL's manifest. Never downloads, never reinstalls.</summary>
    internal async Task ScanAndReconcileAsync(CancellationToken ct)
    {
        var egl = await _c.Egl.ScanAsync(ct);
        _c.Hub.Mutate(s => s with { Egl = egl });

        var rec = _c.Records.Current;
        if (rec?.EglInstallationGuid is null)
        {
            return;
        }

        var drift = _c.Egl.CheckDrift(rec);
        _c.Log.Info("egl", $"drift: {drift.Kind} (egl={drift.EglVersion}, glider={drift.GliderVersion})");
        switch (drift.Kind)
        {
            case DriftKind.EglNewer:
                if (_c.Hub.ActiveJobs().Count > 0 || _c.Egl.IsBusyWith(rec.InstallPath))
                {
                    return; // Try again next start; never race Legendary's lock or EGL.
                }

                // EGL updated/repaired the folder: re-import picks up its .egstore manifest. No download.
                var outcome = await _c.Legendary.ImportAsync(ServiceContext.App, rec.InstallPath, ct);
                if (outcome.Success)
                {
                    await _c.SyncRecordAsync(ct, r => r with { EglInstallationGuid = rec.EglInstallationGuid });
                }
                else
                {
                    _c.Log.Warn("egl", $"re-import after EGL update failed: {outcome.Error?.Code}");
                }

                break;

            case DriftKind.GliderNewer:
                if (_c.Settings.EglSync)
                {
                    var installed = await _c.Legendary.GetInstalledAsync(ServiceContext.App, ct);
                    await _c.Egl.WriteBackAsync(_c.BuildWriteBack(rec, installed, _c.LastGameInfo), ct);
                }

                break;

            case DriftKind.Unreconcilable:
                // A quick verify (never a reinstall), queued once persisted jobs are restored.
                _verifyAfterRestore = true;
                await _c.SaveRecordAsync(rec with { NeedsVerification = true });
                break;

            case DriftKind.EglMissing:
                // EGL forgot the install (uninstalled there, or moved): it's ours alone now.
                await _c.SaveRecordAsync(rec with { EglInstallationGuid = null });
                break;
        }
    }
}
