using Glider.Core.Saves;

namespace Glider.Core.Tests.Saves;

/// <summary>The 3-way conflict check as a table: every row is a (local, cloud, ledger) situation and the only safe answer.</summary>
public class SaveSyncPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static SaveFileEntry F(string path, long size, DateTimeOffset mtime, string? hash = "h") => new(path, size, mtime.ToFileTime(), hash);

    private static LocalSaveSnapshot Local(params SaveFileEntry[] files) => new(files);

    private static CloudSaveSnapshot Cloud(params DateTimeOffset[] saves) => new([.. saves.Select(CloudManifests.NameFor)]);

    private static SaveLedgerEntry Ledger(IEnumerable<SaveFileEntry> files, params DateTimeOffset[] cloud) =>
        new("C:/saves", T0, [.. files], [.. cloud.Select(CloudManifests.NameFor)], "upload");

    public static TheoryData<string, string> Table => new()
    {
        { "no save anywhere", "NoSave" },
        { "first sync: local only", "Upload" },
        { "first sync: cloud only", "Download" },
        { "first sync: both, different times", "Conflict" },
        { "first sync: both, 30 s apart", "InSync" },
        { "first sync: both, 61 s apart", "Conflict" },
        { "neither changed", "InSync" },
        { "local changed only", "Upload" },
        { "cloud changed only", "Download" },
        { "both changed", "Conflict" },
        { "both changed, same moment (within 60 s)", "InSync" },
        { "clock skew: local mtime far in the future, nothing changed", "InSync" },
        { "clock skew: local changed but its mtime is older than the cloud head", "Upload" },
        { "clock skew: cloud changed but its name is older than the local mtime", "Download" },
        { "local file deleted (one of two)", "Upload" },
        { "local folder emptied, cloud unchanged", "Download" },
        { "local folder emptied, cloud changed", "Download" },
        { "cloud emptied, local unchanged", "Upload" },
        { "cloud emptied, local changed", "Upload" },
        { "touched file only (new mtime, same content)", "InSync" },
        { "same mtime, different size", "Upload" },
        { "an older cloud save was cleaned, head intact", "InSync" },
        { "the cloud head was removed", "Download" },
        { "empty local folder and no cloud, with a ledger", "NoSave" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Decides(string scenario, string expectedName)
    {
        var expected = Enum.Parse<SaveDecisionKind>(expectedName);
        var (local, cloud, ledger) = Build(scenario);
        var decision = SaveSyncPlanner.Decide(local, cloud, ledger);
        Assert.True(decision.Kind == expected, $"{scenario}: expected {expected} but got {decision.Kind} ({decision.Reason})");
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    private static (LocalSaveSnapshot, CloudSaveSnapshot, SaveLedgerEntry?) Build(string scenario)
    {
        var a = F("slot1.sav", 100, T0);
        var b = F("slot2.sav", 200, T0);
        var synced = Ledger([a, b], T0);

        return scenario switch
        {
            "no save anywhere" => (Local(), Cloud(), null),
            "first sync: local only" => (Local(a), Cloud(), null),
            "first sync: cloud only" => (Local(), Cloud(T0), null),
            "first sync: both, different times" => (Local(a), Cloud(T0.AddDays(3)), null),
            "first sync: both, 30 s apart" => (Local(a), Cloud(T0.AddSeconds(30)), null),
            "first sync: both, 61 s apart" => (Local(a), Cloud(T0.AddSeconds(61)), null),
            "neither changed" => (Local(a, b), Cloud(T0), synced),
            "local changed only" => (Local(F("slot1.sav", 150, T0.AddHours(1), "new"), b), Cloud(T0), synced),
            "cloud changed only" => (Local(a, b), Cloud(T0, T0.AddHours(2)), synced),
            "both changed" => (Local(F("slot1.sav", 150, T0.AddHours(1), "new"), b), Cloud(T0, T0.AddHours(2)), synced),
            "both changed, same moment (within 60 s)" => (Local(F("slot1.sav", 150, T0.AddHours(2).AddSeconds(20), "new"), b), Cloud(T0, T0.AddHours(2)), synced),
            "clock skew: local mtime far in the future, nothing changed" => (Local(F("slot1.sav", 100, T0.AddDays(400)), F("slot2.sav", 200, T0.AddDays(400))), Cloud(T0), Ledger([F("slot1.sav", 100, T0.AddDays(400)), F("slot2.sav", 200, T0.AddDays(400))], T0)),
            "clock skew: local changed but its mtime is older than the cloud head" => (Local(F("slot1.sav", 150, T0.AddDays(-2), "new"), b), Cloud(T0), synced),
            "clock skew: cloud changed but its name is older than the local mtime" => (Local(F("slot1.sav", 100, T0.AddDays(30)), F("slot2.sav", 200, T0.AddDays(30))), Cloud(T0, T0.AddDays(1)), Ledger([F("slot1.sav", 100, T0.AddDays(30)), F("slot2.sav", 200, T0.AddDays(30))], T0)),
            "local file deleted (one of two)" => (Local(a), Cloud(T0), synced),
            "local folder emptied, cloud unchanged" => (Local(), Cloud(T0), synced),
            "local folder emptied, cloud changed" => (Local(), Cloud(T0, T0.AddHours(1)), synced),
            "cloud emptied, local unchanged" => (Local(a, b), Cloud(), synced),
            "cloud emptied, local changed" => (Local(F("slot1.sav", 150, T0.AddHours(1), "new"), b), Cloud(), synced),
            "touched file only (new mtime, same content)" => (Local(F("slot1.sav", 100, T0.AddMinutes(30), "h"), b), Cloud(T0), synced),
            "same mtime, different size" => (Local(F("slot1.sav", 101, T0, "h2"), b), Cloud(T0), synced),
            "an older cloud save was cleaned, head intact" => (Local(a, b), Cloud(T0.AddDays(1)), Ledger([a, b], T0, T0.AddDays(1))),
            "the cloud head was removed" => (Local(a, b), Cloud(T0), Ledger([a, b], T0, T0.AddDays(1))),
            "empty local folder and no cloud, with a ledger" => (Local(), Cloud(), synced),
            _ => throw new InvalidOperationException(scenario),
        };
    }

    [Fact]
    public void TheTieWindowIsExactlyLegendarys60Seconds()
    {
        var a = F("x", 1, T0);
        Assert.Equal(SaveDecisionKind.InSync, SaveSyncPlanner.Decide(Local(a), Cloud(T0.AddSeconds(59)), null).Kind);
        Assert.Equal(SaveDecisionKind.Conflict, SaveSyncPlanner.Decide(Local(a), Cloud(T0.AddSeconds(60)), null).Kind);
        Assert.Equal(SaveDecisionKind.InSync, SaveSyncPlanner.Decide(Local(a), Cloud(T0.AddSeconds(-59)), null).Kind);
        Assert.Equal(SaveDecisionKind.Conflict, SaveSyncPlanner.Decide(Local(a), Cloud(T0.AddSeconds(-60)), null).Kind);
    }

    [Fact]
    public void AFirstSyncThatDiffersIsNeverResolvedByWhichSideIsNewer()
    {
        // Legendary would pick the newer side here and overwrite the other. Glider can't know which one holds the progress.
        var cloudNewer = SaveSyncPlanner.Decide(Local(F("x", 1, T0)), Cloud(T0.AddDays(5)), null);
        var localNewer = SaveSyncPlanner.Decide(Local(F("x", 1, T0.AddDays(5))), Cloud(T0), null);
        Assert.Equal(SaveDecisionKind.Conflict, cloudNewer.Kind);
        Assert.Equal(SaveDecisionKind.Conflict, localNewer.Kind);
    }

    [Fact]
    public void FilePathsCompareCaseInsensitively_LikeTheFileSystem()
    {
        var ledger = new[] { F("Slot1.SAV", 100, T0) };
        Assert.False(SaveSyncPlanner.LocalChanged(ledger, [F("slot1.sav", 100, T0)]));
        Assert.True(SaveSyncPlanner.LocalChanged(ledger, [F("slot2.sav", 100, T0)]));
    }

    [Fact]
    public void AnUnknownHashNeverHidesAChange()
    {
        var ledger = new[] { F("a", 10, T0, null) };
        Assert.True(SaveSyncPlanner.LocalChanged(ledger, [F("a", 10, T0.AddMinutes(1), "x")]));
        Assert.False(SaveSyncPlanner.LocalChanged(ledger, [F("a", 10, T0, null)])); // identical size and mtime
    }

    [Fact]
    public void AnUploadLandsWhenANewManifestAppearsOrTheExpectedNameIsListed()
    {
        var before = new[] { CloudManifests.NameFor(T0) };
        var fresh = T0.AddHours(3).AddMilliseconds(250);
        Assert.False(SaveSyncPlanner.UploadLanded(before, before, fresh)); // the PUT was dropped
        Assert.True(SaveSyncPlanner.UploadLanded(before, [.. before, CloudManifests.NameFor(T0.AddHours(3))], fresh));
        Assert.True(SaveSyncPlanner.UploadLanded(before, [.. before, "something-else.manifest"], fresh)); // any new save
        // a re-upload of the same second replaces the manifest of that name
        Assert.True(SaveSyncPlanner.UploadLanded([CloudManifests.NameFor(T0)], [CloudManifests.NameFor(T0)], T0.AddMilliseconds(400)));
    }

    [Fact]
    public void AnUploadAcrossADstBoundaryMayBeNamedAnHourOff()
    {
        var mtime = new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero);
        var before = new[] { CloudManifests.NameFor(mtime.AddDays(-10)) };
        string[] after = [.. before, CloudManifests.NameFor(mtime.AddHours(1))];
        Assert.True(SaveSyncPlanner.UploadLanded(before, after, mtime));
        Assert.Contains(CloudManifests.NameFor(mtime.AddHours(-1)), SaveSyncPlanner.ExpectedUploadNames(mtime));
    }

    [Fact]
    public void ADownloadLandedOnlyWhenFilesCarryAManifestsTime()
    {
        var cloud = Cloud(T0, T0.AddHours(2));
        Assert.False(SaveSyncPlanner.DownloadLanded(Local(), cloud)); // Legendary deleted everything and wrote nothing
        Assert.False(SaveSyncPlanner.DownloadLanded(Local(F("x", 1, T0.AddHours(1))), cloud)); // not a manifest time
        Assert.True(SaveSyncPlanner.DownloadLanded(Local(F("x", 1, T0.AddHours(2)), F("y", 1, T0.AddHours(2))), cloud));
        Assert.True(SaveSyncPlanner.DownloadLanded(Local(F("x", 1, T0.AddHours(2).AddSeconds(2))), cloud)); // within 3 s
        Assert.False(SaveSyncPlanner.DownloadLanded(Local(F("x", 1, T0.AddHours(2).AddSeconds(30))), cloud));
    }

    [Fact]
    public void ManifestNamesRoundTrip()
    {
        var name = CloudManifests.NameFor(T0);
        Assert.Equal("2026.11.10-12.00.00.manifest", name);
        Assert.Equal(T0, CloudManifests.TryParse(name));
        Assert.Null(CloudManifests.TryParse("garbage.manifest"));
    }
}
