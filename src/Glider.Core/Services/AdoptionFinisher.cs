using Glider.Core.Contracts;
using Glider.Core.Egl;
using Glider.Core.Game;

namespace Glider.Core.Services;

/// <summary>What finishing an import decided for one game.</summary>
internal sealed record AdoptionResult(
    InstallRecord Record,
    /// <summary>A verify (and repair of what it finds) follows.</summary>
    bool Verify,
    JobOutcome Outcome,
    TagPlan? Tags,
    /// <summary>Why a verify is needed, in words for the user (null when none).</summary>
    string? VerifyReason);

/// <summary>
/// The second half of "adopt an existing install" (docs/egl.md §Adoption): after <c>legendary import</c> registered the folder,
/// carry over what Legendary doesn't — the install tags — and decide honestly whether the folder needs a verify.
///
/// Root cause of the 35 GB repair seen right after adopting Fortnite from EGL (Glider 0.1.5, D:\EpicLibrary\Fortnite):
/// (1) EGL had left the folder mid-update — <c>.egstore</c> held the 43.00 manifest, <c>.egstore\Pending</c> the 43.10 manifest
/// and <c>.egstore\bps</c> ~8 GB of staged files — and had no <c>.mancpn</c>, so Legendary ignored the folder's manifest, recorded
/// the <i>latest</i> build (43.10) and flagged it unverified; the 190 files EGL hadn't updated yet then failed verification and
/// Legendary 0.21.1 <c>repair</c> re-downloads whole files (no chunk reuse). That part is a real, unfinished update.
/// (2) <c>import</c> records no install tags, so <c>verify</c> counted all 247 optional-pack files EGL never installed as missing, and
/// the repair's <c>--skip-sdl</c> then trimmed the folder to the required packs ("Deleting now untagged files").
/// This class fixes (2) and reports (1) for what it is instead of calling it corruption.
/// </summary>
internal sealed class AdoptionFinisher
{
    private readonly ServiceContext _c;

    public AdoptionFinisher(ServiceContext context) => _c = context;

    public async Task<AdoptionResult?> FinishAsync(JobInfo job, VerifyMode mode, CancellationToken ct)
    {
        var app = job.AppName;
        var installed = await _c.Legendary.GetInstalledAsync(app, ct);
        if (installed is null)
        {
            return null;
        }

        var path = installed.InstallPath;
        var item = _c.Egl.FindByLocation(path, app) ?? (job.TargetPath is { } t ? _c.Egl.FindByLocation(t, app) : null);
        var info = SafeInspect(path, app);

        var tags = await CarryTagsAsync(app, installed, item, ct);

        await _c.Library.ReloadAsync();
        var latest = _c.Library.Summary(app)?.LatestVersion;
        var eglBuild = item?.Version is { Length: > 0 } iv ? iv : info?.EglManifestVersion;
        var recordedOtherThanEgl = eglBuild is not null && !string.Equals(eglBuild, installed.Version, StringComparison.Ordinal);
        var unfinished = info is { StagingPresent: true } || item is { IsIncomplete: true } || item is { NeedsValidation: true };

        // Auto verifies only what is actually in doubt: Legendary's own flag (no EGL metadata, or an install in progress), an
        // unfinished EGL operation, a recorded build that isn't the folder's, or a manifest we couldn't confirm. A complete EGL
        // install that is merely behind the latest build needs no verify: the next update patches it from its own manifest.
        var verify = mode != VerifyMode.Auto
                     || installed.NeedsVerification
                     || unfinished
                     || recordedOtherThanEgl
                     || (item is null && (info is null || info.VerifyRecommended));
        var reason = !verify ? null
            : unfinished ? UnfinishedReason(item, info, installed.Version)
            : recordedOtherThanEgl ? $"The folder holds build {Short(eglBuild!)} but Epic's latest, {Short(installed.Version)}, was recorded."
            : mode != VerifyMode.Auto ? "You asked for a verification."
            : "Epic Games Launcher left nothing to confirm the files by.";

        var old = _c.Games.Get(app).Install;
        var rec = new InstallRecord(app, path, installed.Version, installed.InstallSizeBytes, old?.InstalledAt ?? _c.Now,
            AdoptedFromEgl: item is not null, EglInstallationGuid: item?.InstallationGuid ?? old?.EglInstallationGuid, NeedsVerification: verify);
        var outcome = verify
            ? new JobOutcome(OutcomeKind.NeedsRepair, $"Imported. {reason} Glider is checking which files need attention.")
            : new JobOutcome(OutcomeKind.Success, latest is not null && eglBuild is not null && !string.Equals(eglBuild, latest, StringComparison.Ordinal)
                ? $"Imported at build {Short(eglBuild)}; an update is available."
                : "Imported");
        return new AdoptionResult(rec, verify, outcome, tags, reason);
    }

    /// <summary>Tags first: every later verify, repair or update reads them from config.ini.</summary>
    private async Task<TagPlan?> CarryTagsAsync(string app, LegendaryInstalledGame installed, EglItem? item, CancellationToken ct)
    {
        try
        {
            var manifestPath = _c.Legendary.GetInstalledManifestPath(app, installed.Version);
            if (manifestPath is null)
            {
                return null;
            }

            var manifest = await Task.Run(() => EpicManifestReader.TryReadFile(manifestPath), ct);
            if (manifest is null)
            {
                _c.Log.Info("egl", $"{app}: manifest {manifestPath} not readable; leaving install tags to Legendary");
                return null;
            }

            var plan = await Task.Run(() => InstallTagPlanner.Plan(manifest, installed.InstallPath, item?.InstallTags, ct), ct);
            if (plan is null)
            {
                return null;
            }

            await _c.Legendary.SetInstallTagsAsync(app, plan.Tags, overwrite: true, ct);
            _c.Log.Info("egl", $"{app}: install tags from {plan.Source} ({plan.Tags.Count - 1} of {plan.TagsInBuild}); {plan.FilesWantedMissing} of {plan.FilesWanted} wanted files absent, {plan.FilesOutsideTagsOnDisk} on disk outside them");
            return plan;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _c.Log.Warn("egl", $"couldn't carry install tags of {app}", e);
            return null;
        }
    }

    private EglAdoptionInfo? SafeInspect(string path, string app)
    {
        try
        {
            return _c.Egl.InspectForAdoption(path, app);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _c.Log.Warn("egl", $"couldn't inspect {path}", e);
            return null;
        }
    }

    private static string UnfinishedReason(EglItem? item, EglAdoptionInfo? info, string recorded)
    {
        var target = info?.PendingVersion ?? (item is { IsIncomplete: false } ? null : recorded);
        return target is null
            ? "Epic Games Launcher had an operation unfinished on it."
            : $"Epic Games Launcher hadn't finished updating it to {Short(target)}.";
    }

    /// <summary>"++Fortnite+Release-43.10-CL-60118918-Windows" → "43.10", other formats unchanged.</summary>
    internal static string Short(string build)
    {
        var m = System.Text.RegularExpressions.Regex.Match(build, @"Release-(?<v>[0-9][^-]*)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
        return m.Success ? m.Groups["v"].Value : build;
    }
}
