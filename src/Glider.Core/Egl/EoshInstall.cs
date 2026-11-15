namespace Glider.Core.Egl;

/// <summary>
/// One install as EGL 20+ records it through EOSH, its install helper service: <c>InstalledItems\&lt;InstallationGuid&gt;.egi</c>
/// (JSON, <c>{"v4": {…}}</c>). EGL loads no <c>.item</c> files once EOSH is enabled ("LoadItemFiles: Loaded 0 item files",
/// "LauncherUsingEOSH=true") and only mirrors installs back into <c>Data\Manifests</c>, which can fail; Legendary and Heroic
/// read only the mirror, so an EOSH-only install is invisible to them.
/// <para>
/// <see cref="ToItemJson"/> maps the record onto the <c>.item</c> keys <see cref="EglManifest"/> knows, so every reader of EGL
/// installs (scan, import, busy checks, drift) treats both the same. The result is read-only: Glider never writes it back, and
/// never writes a <c>.item</c> for an install EOSH owns. The record also carries signed CDN URLs (<c>manifestUris</c>); they are
/// not copied, logged or stored.
/// </para>
/// </summary>
internal static class EoshInstall
{
    /// <summary>EOS's own components (overlay, service, support) live in a sibling folder, but never count them as games.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase) { "overlay", "service", "support" };

    /// <summary>
    /// The <c>.item</c>-shaped dictionary for one <c>.egi</c>, or null when it isn't a game install (no artifact, folder or
    /// installation id; an EOS component). <paramref name="reason"/> says why for the log.
    /// </summary>
    public static Dictionary<string, object?>? ToItemJson(Dictionary<string, object?> egi, out string? reason)
    {
        reason = null;
        var v = egi.GetValueOrDefault("v4") as Dictionary<string, object?>;
        if (v is null)
        {
            // Unknown schema: say so instead of guessing (a newer EGL may rename keys).
            reason = "no \"v4\" record (unknown .egi format)";
            return null;
        }

        var id = Str(v, "installationId");
        var app = Str(v, "artifactId");
        var dir = Str(v, "dir");
        if (id.Length == 0 || app.Length == 0 || dir.Length == 0)
        {
            reason = "missing installationId, artifactId or dir";
            return null;
        }

        if (NotGames.Contains(id))
        {
            reason = "an EOS component, not a game";
            return null;
        }

        var data = v.GetValueOrDefault("manifestData") as Dictionary<string, object?> ?? [];
        var state = Str(v, "state");
        var installed = string.Equals(state, "Installed", StringComparison.OrdinalIgnoreCase);
        var tags = (v.GetValueOrDefault("tags") as List<object?> ?? []).OfType<string>().Cast<object?>().ToList();
        var metaDir = Str(v, "metaDir");

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["FormatVersion"] = 0L,
            ["AppName"] = app,
            ["MainGameAppName"] = app,
            ["InstallationGuid"] = id,
            ["InstallLocation"] = EglPath.TrimTrailingSeparators(dir),
            ["ManifestLocation"] = metaDir.Length > 0 ? metaDir : Path.Combine(dir, ".egstore"),
            ["StagingLocation"] = Path.Combine(metaDir.Length > 0 ? metaDir : Path.Combine(dir, ".egstore"), "bps"),
            ["AppVersionString"] = Str(data, "version"),
            ["LaunchExecutable"] = Str(data, "launchExe"),
            ["LaunchCommand"] = Str(data, "launchCommand").Trim(),
            ["InstallSize"] = data.GetValueOrDefault("buildSize") is long size ? size : 0L,
            ["InstallTags"] = tags,
            ["CatalogNamespace"] = Str(v, "sandboxId"),
            ["CatalogItemId"] = Str(v, "itemId"),
            ["MandatoryAppFolderName"] = EglPath.LeafName(dir),
            // Anything but "Installed" (installing, updating, a pending manifest, staged version) means EGL is mid-operation.
            ["bIsIncompleteInstall"] = !installed || Str(v, "stagedVersion").Length > 0 || Str(v, "pendingManifestPath") is { Length: > 0 } p && File.Exists(p),
            ["bNeedsValidation"] = false,
            ["EoshState"] = state,
        };
    }

    private static string Str(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) as string ?? "";
}
