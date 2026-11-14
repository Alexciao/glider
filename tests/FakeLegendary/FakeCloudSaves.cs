using System.Globalization;
using System.Text.Json.Nodes;

namespace FakeLegendary;

/// <summary>
/// `list-saves` / `sync-saves` against a fake Epic cloud kept on disk (<c>fake/cloud/&lt;app&gt;/&lt;manifest&gt;/&lt;files&gt;</c>), following
/// Legendary 0.21.1's cli.py/core.py: the decision from the newest local mtime against the newest manifest name (60 s tie
/// window), a download that deletes the local files first and writes every file with the manifest time as mtime, an upload named
/// after the newest local mtime whose PUT is never checked, installed.json's save_path (set by --accept-path / --save-path), the
/// log lines (stderr) and the `list-saves` listing (stdout).
/// </summary>
internal static partial class Program
{
    private const string ManifestFormat = "yyyy.MM.dd-HH.mm.ss";

    private static string CloudDir(string app) => Path.Combine(_config, "fake", "cloud", app);

    private static List<string> Manifests(string app) =>
        Directory.Exists(CloudDir(app))
            ? [.. Directory.GetDirectories(CloudDir(app)).Select(d => Path.GetFileName(d)).OrderBy(n => n, StringComparer.Ordinal)]
            : [];

    private static DateTime ManifestTime(string name) =>
        DateTime.ParseExact(name, ManifestFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static void CloudSaves(Dictionary<string, string> kv)
    {
        var app = _positional.Count > 0 ? _positional[0] : "";
        if (kv.ContainsKey("list"))
        {
            Err("[cli] INFO: Getting list of saves...");
            Out("Save games:");
            if (Manifests(app).Count > 0)
            {
                Out($"- {app} (\"{app}\")");
                foreach (var m in Manifests(app))
                {
                    Out($" + {m}.manifest");
                }
            }

            return;
        }

        SyncSaves(app, kv.GetValueOrDefault("fault", ""));
    }

    private static void SyncSaves(string app, string fault)
    {
        var downloadOnly = _options.ContainsKey("--skip-upload");
        var uploadOnly = _options.ContainsKey("--skip-download");
        var forceUpload = _options.ContainsKey("--force-upload");
        var forceDownload = _options.ContainsKey("--force-download");
        var acceptPath = _options.ContainsKey("--accept-path");

        var installedFile = Path.Combine(_config, "installed.json");
        var root = File.Exists(installedFile) ? JsonNode.Parse(File.ReadAllText(installedFile))!.AsObject() : new JsonObject();
        if (root[app] is not JsonObject game)
        {
            Err($"[cli] CRITICAL: Game not installed: {app}");
            return;
        }

        var manifests = Manifests(app);
        Err($"[cli] INFO: Got {(manifests.Count > 0 ? 1 : 0)} remote save game(s)");
        Err($"[cli] INFO: Checking \"{app}\" ({app})");

        var savePath = game["save_path"]?.GetValue<string>();
        if (_options.TryGetValue("--save-path", out var overridePath) && overridePath != "true")
        {
            game["save_path"] = savePath = overridePath;
            File.WriteAllText(installedFile, root.ToJsonString());
        }

        if (string.IsNullOrEmpty(savePath))
        {
            // -y without --accept-path skips; with it the computed path is stored (the fake computes it from fake/savepath.<app>).
            if (!acceptPath)
            {
                Err("[cli] INFO: Save path for this title has not been set, skipping due to --yes");
                return;
            }

            var computed = File.Exists(Path.Combine(_config, "fake", $"savepath.{app}")) ? File.ReadAllText(Path.Combine(_config, "fake", $"savepath.{app}")).Trim() : "{AppData}/x";
            Err($"[cli] INFO: Computed save path: \"{computed}\"");
            if (computed.Contains('{', StringComparison.Ordinal) || computed.Contains('%', StringComparison.Ordinal))
            {
                Err("[cli] WARNING: Path contains unprocessed variables, please enter the correct path manually.");
                return;
            }

            Directory.CreateDirectory(computed);
            game["save_path"] = savePath = computed;
            File.WriteAllText(installedFile, root.ToJsonString());
        }

        // check_savegame_state: newest mtime of every file against the newest manifest name
        DateTime? local = null;
        if (Directory.Exists(savePath))
        {
            foreach (var f in Directory.EnumerateFiles(savePath, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (local is null || t > local)
                {
                    local = t;
                }
            }
        }

        DateTime? remote = manifests.Count > 0 ? ManifestTime(manifests[^1]) : null;
        string state;
        if (local is null && remote is null)
        {
            state = "none";
        }
        else if (remote is null)
        {
            state = "local";
        }
        else if (local is null)
        {
            state = "remote";
        }
        else
        {
            var d = (local.Value - remote.Value).TotalSeconds;
            state = Math.Abs(d) < 60 ? "same" : d > 0 ? "local" : "remote";
        }

        if (state == "none")
        {
            Err("[cli] INFO: No cloud or local savegame found.");
            return;
        }

        if (state == "same" && !(forceUpload || forceDownload))
        {
            Err($"[cli] INFO: Save game for \"{app}\" is up to date, skipping...");
            return;
        }

        if ((state == "remote" && !forceUpload) || forceDownload)
        {
            if (uploadOnly)
            {
                Err("[cli] INFO: Save game downloading is disabled, skipping...");
                return;
            }

            Err("[cli] INFO: Downloading remote savegame...");
            Download(app, savePath, manifests[^1], fault);
        }
        else if (state == "local" || forceUpload)
        {
            if (downloadOnly)
            {
                Err("[cli] INFO: Save game uploading is disabled, skipping...");
                return;
            }

            Err("[cli] INFO: Uploading local savegame...");
            Upload(app, savePath, local, fault);
        }
    }

    private static void Download(string app, string savePath, string manifest, string fault)
    {
        // clean_dir=True: every local file goes first
        if (Directory.Exists(savePath))
        {
            foreach (var f in Directory.EnumerateFiles(savePath, "*", SearchOption.AllDirectories).ToList())
            {
                File.Delete(f);
            }
        }

        Err("[cli] INFO: Deleting old save files...");
        if (fault == "killafterdelete")
        {
            Environment.Exit(1);
        }

        if (fault == "failafterdelete")
        {
            Err("[cli] ERROR: Download failed, status code: 500");
            Err("[cli] INFO: Successfully completed savegame download."); // Legendary prints this regardless
            return;
        }

        var at = ManifestTime(manifest);
        var src = Path.Combine(CloudDir(app), manifest);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(savePath, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, at); // "set modified time to savegame creation timestamp"
        }

        Err("[cli] INFO: Successfully completed savegame download.");
    }

    private static void Upload(string app, string savePath, DateTime? local, string fault)
    {
        var files = Directory.Exists(savePath) ? Directory.EnumerateFiles(savePath, "*", SearchOption.AllDirectories).ToList() : [];
        if (files.Count == 0 || local is null)
        {
            Err("[cli] INFO: No files to upload. If you believe this is incorrect run command with \"--disable-filters\"");
            return;
        }

        Err("[cli] INFO: Starting upload...");
        if (fault != "dropupload")
        {
            var t = local.Value;
            var name = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Utc).ToString(ManifestFormat, CultureInfo.InvariantCulture);
            var dest = Path.Combine(CloudDir(app), name);
            if (Directory.Exists(dest))
            {
                Directory.Delete(dest, recursive: true);
            }

            foreach (var f in files)
            {
                var target = Path.Combine(dest, Path.GetRelativePath(savePath, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, overwrite: true);
            }
        }

        Err("[cli] INFO: Finished uploading savegame."); // the PUT status is never checked
    }
}
