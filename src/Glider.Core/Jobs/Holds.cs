using System.Text.RegularExpressions;
using Glider.Core.Contracts;

namespace Glider.Core.Jobs;

/// <summary>
/// Tells why a job is waiting from the sentence Core wrote for it (<see cref="JobInfo.BlockedReason"/>), so the UI gets a kind
/// and a game title instead of text to take apart. The sentences are Core's own (JobHooks, EglService, QueuePolicy); this is the
/// one place that reads them back.
/// </summary>
internal static partial class Holds
{
    [GeneratedRegex(@"^(?:Waiting for (?<t>.+?) to close|Paused while (?<t>.+?) is running)\.?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Game();

    public static HoldInfo FromBlockedReason(string reason)
    {
        reason = reason.Trim();
        if (reason.Contains("internet connection", StringComparison.OrdinalIgnoreCase))
        {
            return new HoldInfo(HoldKind.Offline, null);
        }

        if (reason.Contains("Epic Games Launcher", StringComparison.OrdinalIgnoreCase))
        {
            return new HoldInfo(HoldKind.Egl, null);
        }

        if (Game().Match(reason) is { Success: true } m)
        {
            // "Waiting for games to close" (the overlay) names no game.
            var title = m.Groups["t"].Value;
            return new HoldInfo(HoldKind.Game, title.Equals("games", StringComparison.OrdinalIgnoreCase) ? null : title);
        }

        return new HoldInfo(HoldKind.Other, reason);
    }
}
