namespace Glider.Core.Saves;

/// <summary>
/// The 3-way conflict check, as pure functions. Legendary compares one clock (newest local mtime vs newest cloud manifest)
/// and so can overwrite a newer save with an older one (clock skew, a touched log file, a DST change). Glider instead asks
/// two separate questions against the state both sides had at the last sync: did the local folder change, and did the cloud
/// change? Neither answer depends on comparing the two machines' clocks. Only the "first sync" and "converged" cases use the
/// timestamps, with Legendary's own 60 s tie window.
/// </summary>
internal static class SaveSyncPlanner
{
    /// <summary>Legendary's SAME_AGE window (core.py:1137).</summary>
    public static readonly TimeSpan TieWindow = TimeSpan.FromSeconds(60);

    /// <param name="local">The filtered local files, scanned now.</param>
    /// <param name="cloud">The cloud manifests, listed now.</param>
    /// <param name="ledger">The state at the last successful sync, or null if Glider never synced this game.</param>
    public static SaveDecision Decide(LocalSaveSnapshot local, CloudSaveSnapshot cloud, SaveLedgerEntry? ledger)
    {
        var hasLocal = local.HasFiles;
        var hasCloud = cloud.HasSaves;

        if (!hasLocal && !hasCloud)
        {
            return new(SaveDecisionKind.NoSave, "There is no save on either side.");
        }

        if (ledger is null)
        {
            return DecideFirstSync(local, cloud);
        }

        if (!hasLocal)
        {
            // Nothing local to lose: bring the cloud save down (also restores a folder the user cleared).
            return new(SaveDecisionKind.Download, "The local save folder is empty and the cloud has a save.");
        }

        if (!hasCloud)
        {
            return new(SaveDecisionKind.Upload, "The cloud has no save (never uploaded, or removed); the local save is kept and uploaded.");
        }

        var localChanged = LocalChanged(ledger.Files, local.Files);
        var cloudChanged = CloudChanged(ledger.Manifests, cloud.Manifests);

        return (localChanged, cloudChanged) switch
        {
            (false, false) => new(SaveDecisionKind.InSync, "Neither side changed since the last sync."),
            (true, false) => new(SaveDecisionKind.Upload, "Only the local save changed since the last sync."),
            (false, true) => new(SaveDecisionKind.Download, "Only the cloud save changed since the last sync."),
            _ when WithinTie(local, cloud) => new(SaveDecisionKind.InSync, "Both sides changed, but to the same moment (within a minute): treated as the same save."),
            _ => new(SaveDecisionKind.Conflict, "Both the local and the cloud save changed since the last sync."),
        };
    }

    private static SaveDecision DecideFirstSync(LocalSaveSnapshot local, CloudSaveSnapshot cloud)
    {
        if (!cloud.HasSaves)
        {
            return new(SaveDecisionKind.Upload, "First sync: nothing in the cloud yet.");
        }

        if (!local.HasFiles)
        {
            return new(SaveDecisionKind.Download, "First sync: nothing saved on this PC yet.");
        }

        return WithinTie(local, cloud)
            ? new(SaveDecisionKind.InSync, "First sync: both sides carry the same moment (within a minute).")
            : new(SaveDecisionKind.Conflict, "First sync: this PC and the cloud both have a save and Glider can't tell which one is right.");
    }

    /// <summary>True when the newest local file and the newest cloud save are within Legendary's tie window.</summary>
    public static bool WithinTie(LocalSaveSnapshot local, CloudSaveSnapshot cloud) =>
        local.LatestMtime is { } l && cloud.Head is { } c && (l - c).Duration() < TieWindow;

    /// <summary>
    /// Did the local folder change since <paramref name="ledger"/>? A different set of files, a different size, or different
    /// content. A file with only a new mtime counts as unchanged when both hashes are known and equal (a touched file).
    /// </summary>
    public static bool LocalChanged(IReadOnlyList<SaveFileEntry> ledger, IReadOnlyList<SaveFileEntry> current)
    {
        if (ledger.Count != current.Count)
        {
            return true;
        }

        var before = new Dictionary<string, SaveFileEntry>(ledger.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var f in ledger)
        {
            before[f.Path] = f;
        }

        foreach (var now in current)
        {
            if (!before.TryGetValue(now.Path, out var old) || old.Size != now.Size)
            {
                return true;
            }

            if (old.MtimeUtcTicks == now.MtimeUtcTicks)
            {
                continue;
            }

            if (old.Hash is null || now.Hash is null || !string.Equals(old.Hash, now.Hash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Did the cloud change since the ledger? A manifest that was not there before, or the newest one is gone. Older manifests
    /// disappearing (`clean-saves`) is not a change of the current save.
    /// </summary>
    public static bool CloudChanged(IReadOnlyList<string> ledgerManifests, IReadOnlyList<string> currentManifests)
    {
        var before = new HashSet<string>(ledgerManifests, StringComparer.Ordinal);
        if (currentManifests.Any(m => !before.Contains(m)))
        {
            return true;
        }

        if (ledgerManifests.Count == 0)
        {
            return false;
        }

        var head = ledgerManifests.OrderByDescending(m => CloudManifests.TryParse(m) ?? DateTimeOffset.MinValue).ThenByDescending(m => m, StringComparer.Ordinal).First();
        return !currentManifests.Contains(head, StringComparer.Ordinal);
    }

    /// <summary>
    /// Names an upload of a folder whose newest file is <paramref name="latestMtime"/> may have produced. Legendary names the
    /// manifest after that mtime, but converts with a timezone offset fixed at process start, so across a DST boundary the
    /// name can be an hour off (core.py:102,1126).
    /// </summary>
    public static IReadOnlyList<string> ExpectedUploadNames(DateTimeOffset latestMtime)
    {
        var t = latestMtime.ToUniversalTime();
        t = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, TimeSpan.Zero);
        return [CloudManifests.NameFor(t), CloudManifests.NameFor(t.AddHours(-1)), CloudManifests.NameFor(t.AddHours(1))];
    }

    /// <summary>
    /// Was an upload accepted? A manifest that was not listed before the upload now exists, or the one the local mtime would
    /// have produced is listed (a re-upload of the same second replaces a manifest of the same name).
    /// </summary>
    public static bool UploadLanded(IReadOnlyList<string> before, IReadOnlyList<string> after, DateTimeOffset latestMtime)
    {
        var known = new HashSet<string>(before, StringComparer.Ordinal);
        if (after.Any(m => !known.Contains(m)))
        {
            return true;
        }

        var expected = ExpectedUploadNames(latestMtime);
        return after.Any(m => expected.Contains(m, StringComparer.Ordinal));
    }

    /// <summary>
    /// Did a download write a save? Legendary prints "Successfully completed" even when the manifest or its chunks failed, after
    /// it has already deleted the old files. The proof is on disk: files exist and the newest has the mtime of one of the
    /// cloud manifests (Legendary sets every written file to the manifest time).
    /// </summary>
    public static bool DownloadLanded(LocalSaveSnapshot after, CloudSaveSnapshot cloud, TimeSpan? tolerance = null)
    {
        if (!after.HasFiles)
        {
            return false;
        }

        var tol = tolerance ?? TimeSpan.FromSeconds(3);
        var latest = after.LatestMtime!.Value;
        return cloud.Manifests.Select(CloudManifests.TryParse).Any(m => m is { } at && (latest - at).Duration() <= tol);
    }
}
