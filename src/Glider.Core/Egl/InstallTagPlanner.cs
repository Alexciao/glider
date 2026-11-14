namespace Glider.Core.Egl;

public enum TagSource
{
    /// <summary>EGL's own <c>InstallTags</c> (what it installed): authoritative, like Legendary's <c>egl_import</c>.</summary>
    EglItem,

    /// <summary>Worked out from which of the build's files are on disk (no <c>.item</c>, or it lists none).</summary>
    OnDisk,
}

/// <summary>
/// The install tags (components / optional packs) a folder holds, to hand to Legendary as <c>[app] install_tags</c>.
/// <see cref="Tags"/> always contains the untagged bucket (<c>""</c>) first.
/// </summary>
public sealed record TagPlan(
    IReadOnlyList<string> Tags,
    TagSource Source,
    /// <summary>Files the build lists that these tags select.</summary>
    int FilesWanted,
    /// <summary>…of which are not on disk (what a verify/repair against these tags would still have to fetch).</summary>
    int FilesWantedMissing,
    /// <summary>Files outside these tags that are on disk (an update with these tags would delete them as deselected).</summary>
    int FilesOutsideTagsOnDisk,
    /// <summary>Every tag the build has, so callers can tell "everything" from "a subset".</summary>
    int TagsInBuild);

/// <summary>
/// Why adopted installs need <c>install_tags</c> (Glider 0.1.5, Fortnite on D:): <c>legendary import</c> never records them, so
/// <c>verify</c> (cli.py:1281) falls back to <i>every</i> file of the build and reports the optional packs EGL never installed as
/// missing (247 files), and an <c>update</c>/<c>repair --skip-sdl</c> on an SDL game selects only the required packs and then
/// "Deleting now untagged files" (cli.py:1150, core.py:1868) removes what EGL had installed beyond them. Legendary's own EGL
/// import (<c>egl_import</c>, core.py:2012) copies the <c>.item</c>'s tags into config.ini; this does the same, and for folders EGL
/// doesn't describe (located by hand, EGL gone) derives the tags from what is on disk.
/// </summary>
public static class InstallTagPlanner
{
    /// <summary>Null when the build has no tags at all (nothing to carry; Legendary's defaults are right).</summary>
    public static TagPlan? Plan(EpicManifestSummary manifest, string installPath, IReadOnlyList<string>? eglTags, CancellationToken ct = default)
    {
        if (!manifest.HasTags)
        {
            return null;
        }

        var all = manifest.AllTags;
        var fromDisk = DeriveFromDisk(manifest, installPath, ct);
        if (eglTags is { Count: > 0 })
        {
            // EGL's list says what to keep complete (a pack it installed but whose files aren't all there yet stays selected so a repair
            // fetches the rest). The disk never loses a vote: Legendary deletes files of tags that aren't selected, and EGL's list may
            // leave out tags it treats as implicit, so a tag with files on disk is always kept.
            var union = eglTags.Where(t => t.Length > 0).Concat(fromDisk).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            return Describe(manifest, installPath, union, TagSource.EglItem, all.Count, ct);
        }

        return Describe(manifest, installPath, fromDisk, TagSource.OnDisk, all.Count, ct);
    }

    /// <summary>
    /// A tag is installed when one of its files is on disk. A file carrying several tags proves only that one of them is installed, so
    /// those are resolved after the tags with file(s) of their own: a pack whose only on-disk files are shared with an installed pack is
    /// not counted. When nothing explains a shared file, all its tags are kept (an extra tag costs a download, a missing one deletes data).
    /// </summary>
    internal static List<string> DeriveFromDisk(EpicManifestSummary manifest, string installPath, CancellationToken ct)
    {
        var installed = new HashSet<string>(StringComparer.Ordinal);
        var sharedPresent = new List<ManifestFile>();
        foreach (var f in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (f.Tags.Count == 0 || !IsPresent(installPath, f))
            {
                continue;
            }

            if (f.Tags.Count == 1)
            {
                installed.Add(f.Tags[0]);
            }
            else
            {
                sharedPresent.Add(f);
            }
        }

        foreach (var f in sharedPresent)
        {
            if (!f.Tags.Any(installed.Contains))
            {
                foreach (var t in f.Tags)
                {
                    installed.Add(t);
                }
            }
        }

        installed.Remove("");
        return [.. installed.Order(StringComparer.Ordinal)];
    }

    private static TagPlan Describe(EpicManifestSummary manifest, string installPath, List<string> tags, TagSource source, int tagsInBuild, CancellationToken ct)
    {
        var chosen = new HashSet<string>(tags, StringComparer.Ordinal);
        int wanted = 0, wantedMissing = 0, outside = 0;
        foreach (var f in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var selected = f.Tags.Count == 0 || f.Tags.Any(chosen.Contains);
            var present = IsPresent(installPath, f);
            if (selected)
            {
                wanted++;
                if (!present)
                {
                    wantedMissing++;
                }
            }
            else if (present)
            {
                outside++;
            }
        }

        return new TagPlan(tags.Prepend("").ToList(), source, wanted, wantedMissing, outside, tagsInBuild);
    }

    private static bool IsPresent(string installPath, ManifestFile f)
    {
        try
        {
            return File.Exists(Path.Combine(installPath, f.Name.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
