using Glider.Core.Egl;

namespace Glider.Core.Tests.Egl;

public sealed class FortniteVersionTests
{
    [Theory]
    [InlineData("++Fortnite+Release-33.10-CL-45000000-Windows", "++Fortnite+Release-33.10-CL-45000000-Windows", 0)]
    [InlineData("++Fortnite+Release-33.10-CL-45000000-Windows", "++fortnite+release-33.10-cl-45000000-windows", 0)]
    [InlineData("++Fortnite+Release-33.20-CL-46000000-Windows", "++Fortnite+Release-33.10-CL-45000000-Windows", 1)]
    [InlineData("++Fortnite+Release-33.10-CL-45000000-Windows", "++Fortnite+Release-33.20-CL-46000000-Windows", -1)]
    // CL is authoritative even when the release label looks older (hotfix CLs).
    [InlineData("++Fortnite+Release-33.10-CL-46500000-Windows", "++Fortnite+Release-33.20-CL-46000000-Windows", 1)]
    // CL digits compared numerically, not textually.
    [InlineData("++Fortnite+Release-9.10-CL-9999999-Windows", "++Fortnite+Release-10.00-CL-10000000-Windows", -1)]
    // Fallback without CL: numeric components in order.
    [InlineData("33.10", "33.9", 1)]
    [InlineData("Release-33.10.1", "Release-33.10", 1)]
    [InlineData("Release-33.10", "Release-33.11", -1)]
    public void Orders(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(FortniteVersion.Compare(a, b)!.Value));

    [Theory]
    [InlineData(null, "++Fortnite+Release-33.10-CL-45000000-Windows")]
    [InlineData("", "++Fortnite+Release-33.10-CL-45000000-Windows")]
    [InlineData("Live", "Hotfix")]
    // Same CL, different label: ambiguous.
    [InlineData("++Fortnite+Release-33.10-CL-45000000-Windows", "++Fortnite+Release-33.10-CL-45000000-Win64")]
    // Numerically equal but textually different.
    [InlineData("33.10", "33.10.0")]
    // One side with CL, the other not, same release.
    [InlineData("++Fortnite+Release-33.10-CL-45000000-Windows", "Release-33.10")]
    public void Unorderable(string? a, string? b) => Assert.Null(FortniteVersion.Compare(a, b));

    [Fact]
    public void ExtractsChangelist()
    {
        Assert.Equal(45000000, FortniteVersion.Changelist("++Fortnite+Release-33.10-CL-45000000-Windows"));
        Assert.Null(FortniteVersion.Changelist("Release-33.10"));
        Assert.Null(FortniteVersion.Changelist(null));
    }
}

public sealed class EglDriftTests
{
    private static (EglTestTree Tree, string Game) Setup(string eglVersion, string guid = "6A1B3C5D7E9F40A1B2C3D4E5F6A7B8C9",
        Action<System.Text.Json.Nodes.JsonObject>? edit = null)
    {
        var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"), egstoreGuid: guid);
        t.WriteItem(game, eglVersion, guid, edit);
        return (t, game);
    }

    private static Glider.Core.DriftResult Check(EglTestTree t, string game, Glider.Core.GameRecord record)
    {
        var before = EglTreeHasher.Hash(t.Environment, game);
        using var svc = t.CreateService();
        var r = svc.CheckDrift(record);
        EglTreeHasher.AssertSame(before, EglTreeHasher.Hash(t.Environment, game)); // drift checks never write
        return r;
    }

    [Fact]
    public void InSync()
    {
        var (t, game) = Setup(EglTestTree.V3310);
        using (t)
        {
            var r = Check(t, game, Make.For(game, EglTestTree.V3310, "6A1B3C5D7E9F40A1B2C3D4E5F6A7B8C9"));
            Assert.Equal(Glider.Core.DriftKind.InSync, r.Kind);
            Assert.NotNull(r.Egl);
        }
    }

    [Fact]
    public void EglNewerWhenEglUpdatedTheGame()
    {
        var (t, game) = Setup(EglTestTree.V3320);
        using (t)
        {
            var r = Check(t, game, Make.For(game.ToUpperInvariant() + "/", EglTestTree.V3310));
            Assert.Equal(Glider.Core.DriftKind.EglNewer, r.Kind);
            Assert.Equal(EglTestTree.V3320, r.EglVersion);
            Assert.Equal(EglTestTree.V3310, r.GliderVersion);
        }
    }

    [Fact]
    public void GliderNewerWhenGliderUpdatedTheGame()
    {
        var (t, game) = Setup(EglTestTree.V3300);
        using (t)
        {
            Assert.Equal(Glider.Core.DriftKind.GliderNewer, Check(t, game, Make.For(game, EglTestTree.V3310)).Kind);
        }
    }

    [Fact]
    public void EglMissingWhenNoManifestForTheFolder()
    {
        var (t, game) = Setup(EglTestTree.V3310);
        using (t)
        {
            var other = t.CreateGame(t.Drive("Z", "Elsewhere"));
            var r = Check(t, other, Make.For(other, EglTestTree.V3310, "6A1B3C5D7E9F40A1B2C3D4E5F6A7B8C9"));
            Assert.Equal(Glider.Core.DriftKind.EglMissing, r.Kind);
            Assert.Null(r.Egl);
        }

        using var empty = new EglTestTree(eglInstalled: false);
        using var svc = empty.CreateService();
        Assert.Equal(Glider.Core.DriftKind.EglMissing, svc.CheckDrift(Make.For(empty.Drive("D", "F"), EglTestTree.V3310)).Kind);
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("incomplete")]
    [InlineData("unparsable")]
    [InlineData("sameClDifferentLabel")]
    [InlineData("duplicateInstalls")]
    public void UnreconcilableWhenAmbiguous(string scenario)
    {
        var eglVersion = scenario switch
        {
            "unparsable" => "Live",
            "sameClDifferentLabel" => "++Fortnite+Release-33.10-CL-45000000-Win64",
            _ => EglTestTree.V3320,
        };
        var (t, game) = Setup(eglVersion, edit: scenario == "incomplete" ? j => j["bIsIncompleteInstall"] = true : null);
        using (t)
        {
            if (scenario == "duplicateInstalls")
            {
                t.WriteItem(game, EglTestTree.V3310);
            }

            var guid = scenario == "guid" ? "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF" : null;
            Assert.Equal(Glider.Core.DriftKind.Unreconcilable, Check(t, game, Make.For(game, EglTestTree.V3310, guid)).Kind);
        }
    }
}
