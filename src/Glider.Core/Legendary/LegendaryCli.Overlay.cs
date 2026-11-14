using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Overlay;

namespace Glider.Core.Legendary;

// EOS overlay, activation and third-party launch (docs/research/legendary-v0.2.md §7-8; Legendary 0.21.1 cli.py:2072-2430, 746-800).
public sealed partial class LegendaryCli
{
    public async Task<LegendaryOutcome> InstallOverlayAsync(string? path, IProgress<LegendaryProgress> progress, CancellationToken ct)
    {
        var managed = OverlayFiles.ReadInstall(Config) is not null;
        List<string> args = ["-y", "eos-overlay", managed ? "update" : "install"];
        if (!managed && !string.IsNullOrWhiteSpace(path))
        {
            // Legendary's default is ~/Games/.overlay; update ignores --path (core.py:2214-2217).
            args.Add("--path");
            args.Add(FolderMover.Normalize(path));
        }

        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.EosOverlay, args)
        {
            Exclusive = true,
            LowPriority = true, // anonymous client_credentials session: no login(), no installed.json lock
            StallTimeout = _options.StallTimeout,
            Timeout = Timeout.InfiniteTimeSpan,
        }, progress, ct);

        var error = ErrorClassifier.Classify(LegendaryCommandKind.EosOverlay, run);
        if (error is null && OverlayFiles.ReadInstall(Config) is null)
        {
            error = Errors.OverlayFailed("Legendary finished but did not register the overlay.");
        }

        if (error is not null)
        {
            _log.Warn("legendary", $"eos-overlay {(managed ? "update" : "install")} failed: {error.Code} {error.Detail}");
        }

        return new LegendaryOutcome(error is null, error, run.Facts.DownloadSizeBytes, run.Facts.InstallSizeBytes);
    }

    public async Task<LegendaryOutcome> RemoveOverlayAsync(CancellationToken ct)
    {
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.EosOverlay, ["-y", "eos-overlay", "remove"])
        {
            Exclusive = true,
            Timeout = _options.UninstallTimeout,
        }, null, ct);

        // `remove` deletes the folder and overlay_install.json first and only then clears the registry, including HKLM values
        // that a non-elevated process can't touch (lfs/eos.py:130-143): it may end in a PermissionError traceback after the
        // work that matters is done. The files decide, not the exit text. Glider clears HKCU itself afterwards.
        if (OverlayFiles.ReadInstall(Config) is null)
        {
            if (run.Facts.Traceback)
            {
                _log.Info("legendary", "eos-overlay remove ended with an error after removing the overlay (HKLM entries need administrator rights); ignoring it.");
            }

            return new LegendaryOutcome(true, null, null, null);
        }

        var error = ErrorClassifier.Classify(LegendaryCommandKind.EosOverlay, run) is { } e && e.Code != ErrorCode.OverlayFailed
            ? e
            : Errors.OverlayFailed("Legendary did not remove the overlay.");
        return new LegendaryOutcome(false, error, null, null);
    }

    public async Task<LegendaryOutcome> ActivateUplayAsync(CancellationToken ct)
    {
        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.Activate, ["-y", "activate", "--uplay"])
        {
            UsesLogin = true,
            QuietStdout = true,
            Timeout = _options.InfoTimeout,
        }, null, ct);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.Activate, run);
        return new LegendaryOutcome(error is null, error, null, null);
    }

    public async Task<string> GetThirdPartyUriAsync(string appName, ThirdPartyStore store, bool offline, CancellationToken ct)
    {
        var flag = store switch
        {
            ThirdPartyStore.Ea => "--origin",
            ThirdPartyStore.Ubisoft => "--ubisoft",
            _ => throw new GliderException(Errors.InvalidRequest($"No launcher link for {store}")),
        };
        List<string> args = ["launch", appName, flag, "--json"];
        if (offline)
        {
            args.Add("--offline");
        }

        var run = await RunAsync(new LegendaryCommand(LegendaryCommandKind.ThirdPartyLaunch, args)
        {
            CaptureStdout = true,
            SensitiveStdout = true, // the URI embeds a live exchange code
            UsesLogin = !offline,
            LoginPriority = true,
            Timeout = _options.QuickTimeout,
        }, null, ct);
        using var doc = LegendaryFiles.JsonFromStdout(run.Stdout);
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && LegendaryFiles.Str(doc.RootElement, "uri") is { Length: > 0 } uri)
        {
            return uri;
        }

        throw new GliderException(ErrorClassifier.Classify(LegendaryCommandKind.ThirdPartyLaunch, run) ??
                                  Errors.LaunchFailed("Legendary returned no launch link for this game.", null, appName));
    }
}
