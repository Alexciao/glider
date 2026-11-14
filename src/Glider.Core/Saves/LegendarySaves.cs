using System.Text.RegularExpressions;
using Glider.Core.Contracts;
using Glider.Core.Legendary;

namespace Glider.Core.Saves;

/// <summary>What one `sync-saves` run is asked to do. Always a single explicit direction, never Legendary's own choice.</summary>
internal enum SyncMode
{
    /// <summary>`--skip-upload --skip-download --accept-path`: only compute and store the save folder in installed.json.</summary>
    SetupPath,

    /// <summary>`--skip-download --force-upload`.</summary>
    Upload,

    /// <summary>`--skip-upload --force-download`.</summary>
    Download,
}

/// <summary>What `sync-saves` said. It has no JSON and its exit code is 0 on failure, so everything is read from the log lines.</summary>
internal sealed record SyncRun(
    bool UploadFinished,
    bool NothingToUpload,
    bool DownloadReported,
    /// <summary>Legendary logged a failed manifest/chunk request or a missing chunk while downloading (it still prints "Successfully completed").</summary>
    bool DownloadProblems,
    bool PathUnresolved,
    GliderError? Error,
    IReadOnlyList<string> Lines);

/// <summary>The cloud-save commands of the bundled Legendary (docs/research/legendary-v0.2.md §6). Implemented by <see cref="LegendarySavesCli"/>.</summary>
internal interface ILegendarySaves
{
    /// <summary>`list-saves &lt;app&gt;`: the manifest names (newest last). Throws <see cref="GliderException"/> when it fails.</summary>
    Task<CloudSaveSnapshot> ListSavesAsync(string app, CancellationToken ct);

    /// <summary>`sync-saves` in one explicit direction. Errors are in the result; a cancelled run throws.</summary>
    Task<SyncRun> SyncAsync(string app, SyncMode mode, CancellationToken ct);
}

internal sealed partial class LegendarySavesCli : ILegendarySaves
{
    private readonly LegendaryProcessRunner _runner;
    private readonly ILog _log;
    private readonly TimeSpan _listTimeout;
    private readonly TimeSpan _syncTimeout;

    public LegendarySavesCli(LegendaryProcessRunner runner, ILog log, TimeSpan? listTimeout = null, TimeSpan? syncTimeout = null)
    {
        _runner = runner;
        _log = log;
        _listTimeout = listTimeout ?? TimeSpan.FromSeconds(90);
        _syncTimeout = syncTimeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<CloudSaveSnapshot> ListSavesAsync(string app, CancellationToken ct)
    {
        // list-saves also refreshes the game list (cli.py:432-440), so it is a metadata-lane command (the caller holds the lane).
        var run = await _runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.SaveList, ["list-saves", app])
        {
            CaptureStdout = true,
            UsesLogin = true,
            Timeout = _listTimeout,
        }, null, ct).ConfigureAwait(false);

        var error = ErrorClassifier.Classify(LegendaryCommandKind.SaveList, run);
        var snapshot = ParseList(run.Stdout, app, out var sawHeader);
        if (error is null && !sawHeader)
        {
            error = Errors.LegendaryFailed($"list-saves printed no save list (exit code {run.ExitCode}).");
        }

        if (error is not null)
        {
            _log.Warn("saves", $"list-saves {app} failed: {error.Code} {error.Detail}");
            throw new GliderException(error);
        }

        return snapshot;
    }

    public async Task<SyncRun> SyncAsync(string app, SyncMode mode, CancellationToken ct)
    {
        // `-y` is a root option and must come first. A directional call plus `--force-*` runs exactly the side Glider's 3-way
        // check chose (or the user did), whatever Legendary's own newest-mtime guess says.
        IReadOnlyList<string> args = mode switch
        {
            SyncMode.Upload => ["-y", "sync-saves", app, "--skip-download", "--force-upload"],
            SyncMode.Download => ["-y", "sync-saves", app, "--skip-upload", "--force-download"],
            _ => ["-y", "sync-saves", app, "--skip-upload", "--skip-download", "--accept-path"],
        };

        var run = await _runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.SaveSync, args)
        {
            UsesLogin = true,
            Timeout = _syncTimeout,
        }, null, ct).ConfigureAwait(false);

        var lines = run.Tail;
        var result = new SyncRun(
            lines.Any(l => l.Contains("Finished uploading savegame.", StringComparison.Ordinal)),
            lines.Any(l => l.Contains("No files to upload", StringComparison.Ordinal)),
            lines.Any(l => l.Contains("Successfully completed savegame download.", StringComparison.Ordinal)),
            lines.Any(IsDownloadProblem),
            lines.Any(l => l.Contains("unprocessed variables", StringComparison.Ordinal) || l.Contains("skipping due to --yes", StringComparison.Ordinal)),
            ErrorClassifier.Classify(LegendaryCommandKind.SaveSync, run),
            lines);
        _log.Info("saves", $"sync-saves {app} ({mode}): upload={result.UploadFinished} download={result.DownloadReported} problems={result.DownloadProblems} error={result.Error?.Code.ToString() ?? "none"}");
        return result;
    }

    private static bool IsDownloadProblem(string line) =>
        line.Contains("Download failed, status code", StringComparison.Ordinal)
        || line.Contains("Manifest is empty", StringComparison.Ordinal)
        || line.Contains("not in file list, save data may be incomplete", StringComparison.Ordinal)
        || line.Contains("file may be corrupted", StringComparison.Ordinal)
        || line.Contains("No chunks were available", StringComparison.Ordinal);

    /// <summary>
    /// Parses `list-saves` stdout: "Save games:", then per game `- Title ("app")` and one ` + &lt;manifest&gt;` line per save. Only
    /// the requested app's manifests are returned, oldest first.
    /// </summary>
    internal static CloudSaveSnapshot ParseList(string stdout, string app, out bool sawHeader)
    {
        sawHeader = false;
        var manifests = new List<string>();
        var inApp = false;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Save games:", StringComparison.Ordinal))
            {
                sawHeader = true;
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                var m = AppLine().Match(line);
                inApp = m.Success && string.Equals(m.Groups[1].Value, app, StringComparison.Ordinal);
                continue;
            }

            if (inApp && line.StartsWith("+ ", StringComparison.Ordinal))
            {
                var name = line[2..].Trim();
                if (name.Length > 0)
                {
                    manifests.Add(name);
                }
            }
        }

        return new CloudSaveSnapshot([.. manifests.Distinct(StringComparer.Ordinal).OrderBy(n => CloudManifests.TryParse(n) ?? DateTimeOffset.MinValue).ThenBy(n => n, StringComparer.Ordinal)]);
    }

    [GeneratedRegex("\\(\"([^\"]+)\"\\)\\s*$")]
    private static partial Regex AppLine();
}
