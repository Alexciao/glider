using Glider.Core.Contracts;
using Glider.Core.Jobs;

namespace Glider.Core.Tests.Jobs;

/// <summary>The kind of a hold, read once from Core's own wording (the download card composes its copy from the kind).</summary>
public class HoldsTests
{
    [Theory]
    [InlineData("Paused while Fortnite is running", HoldKind.Game, "Fortnite")]
    [InlineData("Paused while Vault & Thorned Co. is running", HoldKind.Game, "Vault & Thorned Co.")]
    [InlineData("Waiting for Fortnite to close", HoldKind.Game, "Fortnite")]
    [InlineData("Waiting for games to close", HoldKind.Game, null)]
    [InlineData("Waiting for an internet connection", HoldKind.Offline, null)]
    [InlineData("Epic Games Launcher is updating this game, so Glider will wait and continue automatically.", HoldKind.Egl, null)]
    [InlineData("Epic Games Launcher is installing this game, so Glider will wait and continue automatically.", HoldKind.Egl, null)]
    public void ReadsTheKindAndTheGame(string reason, HoldKind kind, string? subject) =>
        Assert.Equal(new HoldInfo(kind, subject), Holds.FromBlockedReason(reason));

    [Fact]
    public void AnythingElse_KeepsItsSentence_SoNothingIsLost()
    {
        var hold = Holds.FromBlockedReason("Waiting for Fortnite's cloud saves to sync");
        Assert.Equal(HoldKind.Other, hold.Kind);
        Assert.Equal("Waiting for Fortnite's cloud saves to sync", hold.Subject);
    }
}
