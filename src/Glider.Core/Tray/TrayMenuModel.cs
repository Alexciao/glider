using Glider.Core.Contracts;

namespace Glider.Core.Tray;

public enum TrayCommand
{
    Open,
    /// <summary>Start the game in <see cref="TrayItem.Arg"/>.</summary>
    Launch,
    /// <summary>Close the running game in <see cref="TrayItem.Arg"/>.</summary>
    Close,
    PauseAll,
    ResumeAll,
    /// <summary>Set Settings.DownloadMode to the mode name in <see cref="TrayItem.Arg"/> ("background", "balanced", "full").</summary>
    SetMode,
    OpenDownloads,
    CheckUpdates,
    Quit,
}

public enum TrayItemKind { Command, Separator, Info, Submenu }

/// <summary>One line of the tray menu. The host draws it with native menu calls and maps the chosen item back to a <see cref="TrayCommand"/>.</summary>
public sealed record TrayItem(
    TrayItemKind Kind,
    string Label = "",
    TrayCommand? Command = null,
    string? Arg = null,
    bool Checked = false,
    bool Default = false,
    IReadOnlyList<TrayItem>? Children = null)
{
    public static TrayItem Separator { get; } = new(TrayItemKind.Separator);

    public static TrayItem Cmd(string label, TrayCommand command, string? arg = null, bool isChecked = false, bool isDefault = false) =>
        new(TrayItemKind.Command, label, command, arg, isChecked, isDefault);

    /// <summary>A disabled line of text.</summary>
    public static TrayItem Info(string label) => new(TrayItemKind.Info, label);
}

/// <summary>The menu plus the notification-area tooltip (≤ 127 characters, the Windows limit).</summary>
public sealed record TrayMenu(IReadOnlyList<TrayItem> Items, string Tooltip);

/// <summary>
/// Pure view-model of the tray menu, built from the snapshot and the library: recently played games (up to 5, one click to play),
/// queue status with pause/resume all, the download mode switcher, Downloads, updates and Quit. Cheap enough to rebuild on every menu
/// open (one pass over the library) and for every tooltip refresh (no library access at all).
/// </summary>
public static class TrayMenuModel
{
    public const int MaxRecent = 5;
    public const int MaxTooltip = 127;

    public static TrayMenu Build(AppSnapshot snapshot, IEnumerable<GameSummary> library, JobProgress? live = null)
    {
        var items = new List<TrayItem> { TrayItem.Cmd("Open Glider", TrayCommand.Open, isDefault: true), TrayItem.Separator };

        var recent = RecentGames(library);
        var games = new List<TrayItem>();
        var running = snapshot.Sessions.Where(s => s.State != RunState.Idle).ToList();
        foreach (var s in running.Where(s => recent.All(r => r.AppName != s.AppName)))
        {
            games.Add(TrayItem.Cmd($"Close {s.Title}", TrayCommand.Close, s.AppName));
        }

        foreach (var g in recent)
        {
            games.Add(running.Any(s => s.AppName == g.AppName)
                ? TrayItem.Cmd($"Close {g.Title}", TrayCommand.Close, g.AppName)
                : TrayItem.Cmd($"Play {g.Title}", TrayCommand.Launch, g.AppName));
        }

        if (games.Count > 0)
        {
            items.AddRange(games);
            items.Add(TrayItem.Separator);
        }

        AddQueue(items, snapshot.Queue, live);
        items.Add(TrayItem.Cmd("Open Downloads", TrayCommand.OpenDownloads));
        items.Add(ModeMenu(snapshot));
        items.Add(TrayItem.Separator);
        items.Add(TrayItem.Cmd("Check for updates", TrayCommand.CheckUpdates));
        items.Add(TrayItem.Cmd("Quit Glider", TrayCommand.Quit));
        return new TrayMenu(items, Tooltip(snapshot, live));
    }

    /// <summary>Installed base games and launchable add-ons the user has played, newest first.</summary>
    public static IReadOnlyList<GameSummary> RecentGames(IEnumerable<GameSummary> library) =>
        [.. library
            .Where(g => g.Kind != GameKind.Dlc && g.InstallStatus == InstallStatus.Installed && g.LastPlayedAt is not null && !g.Hidden)
            .OrderByDescending(g => g.LastPlayedAt)
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecent)];

    private static void AddQueue(List<TrayItem> items, QueueState queue, JobProgress? live)
    {
        var active = queue.Jobs.Where(IsActive).ToList();
        if (active.Count == 0)
        {
            return;
        }

        items.Add(TrayItem.Info(QueueLine(queue, active, live)));
        if (queue.Paused || active.Any(j => j.Status == JobStatus.Paused))
        {
            items.Add(TrayItem.Cmd("Resume all downloads", TrayCommand.ResumeAll));
        }

        if (!queue.Paused && active.Any(j => j.Status != JobStatus.Paused))
        {
            items.Add(TrayItem.Cmd("Pause all downloads", TrayCommand.PauseAll));
        }
    }

    private static bool IsActive(JobInfo j) => j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Paused or JobStatus.Waiting;

    private static string QueueLine(QueueState queue, List<JobInfo> active, JobProgress? live)
    {
        var more = active.Count > 1 ? $" (+{active.Count - 1} more)" : "";
        if (queue.Paused)
        {
            return $"Downloads paused · {active.Count} in queue";
        }

        var job = active.FirstOrDefault(j => j.Status == JobStatus.Running);
        if (job is null)
        {
            job = active[0];
            var why = job.Status switch
            {
                JobStatus.Waiting => job.BlockedReason ?? "waiting",
                JobStatus.Paused => "paused",
                _ => "queued",
            };
            return $"{job.Title}: {why}{more}";
        }

        var percent = live is not null && live.JobId == job.Id ? live.Percent : job.Progress?.Percent;
        var pct = percent is { } p ? $" {Math.Clamp((int)Math.Floor(p), 0, 100)}%" : "";
        return $"{Verb(job, live)} {job.Title}{pct}{more}";
    }

    private static string Verb(JobInfo job, JobProgress? live)
    {
        var phase = live is not null && live.JobId == job.Id ? live.Phase : job.Progress?.Phase ?? job.Phase;
        return phase switch
        {
            JobPhase.Verifying => "Verifying",
            JobPhase.Repairing => "Repairing",
            JobPhase.Downloading => "Downloading",
            JobPhase.Installing => "Installing",
            JobPhase.Moving => "Moving",
            JobPhase.Removing => "Removing",
            JobPhase.Importing => "Importing",
            _ => job.Kind switch
            {
                JobKind.Update => "Updating",
                JobKind.Uninstall => "Removing",
                JobKind.Move => "Moving",
                JobKind.Import => "Importing",
                JobKind.Verify or JobKind.Repair => "Repairing",
                _ => "Installing",
            },
        };
    }

    private static TrayItem ModeMenu(AppSnapshot snapshot)
    {
        var chosen = snapshot.Settings.DownloadMode;
        var children = new List<TrayItem>
        {
            TrayItem.Cmd("Background", TrayCommand.SetMode, "background", isChecked: chosen == DownloadMode.Background),
            TrayItem.Cmd("Balanced", TrayCommand.SetMode, "balanced", isChecked: chosen == DownloadMode.Balanced),
            TrayItem.Cmd("Full speed", TrayCommand.SetMode, "full", isChecked: chosen == DownloadMode.Full),
        };
        if (snapshot.Queue.EffectiveMode != chosen)
        {
            var reason = string.IsNullOrWhiteSpace(snapshot.Queue.ModeReason) ? "" : $" ({snapshot.Queue.ModeReason})";
            children.Add(TrayItem.Separator);
            children.Add(TrayItem.Info($"Right now: {ModeName(snapshot.Queue.EffectiveMode)}{reason}"));
        }

        return new TrayItem(TrayItemKind.Submenu, $"Download mode: {ModeName(chosen)}", Children: children);
    }

    public static string ModeName(DownloadMode mode) => mode switch
    {
        DownloadMode.Background => "Background",
        DownloadMode.Full => "Full speed",
        _ => "Balanced",
    };

    /// <summary>Parses the <see cref="TrayItem.Arg"/> of a SetMode item.</summary>
    public static bool TryParseMode(string? arg, out DownloadMode mode)
    {
        mode = DownloadMode.Balanced;
        switch (arg)
        {
            case "background":
                mode = DownloadMode.Background;
                return true;
            case "balanced":
                return true;
            case "full":
                mode = DownloadMode.Full;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Notification-area tooltip: what is running and downloading, within the 127-character limit.</summary>
    public static string Tooltip(AppSnapshot snapshot, JobProgress? live = null)
    {
        var parts = new List<string>();
        var running = snapshot.Sessions.Where(s => s.State != RunState.Idle).ToList();
        if (running.Count == 1)
        {
            parts.Add($"{running[0].Title} running");
        }
        else if (running.Count > 1)
        {
            parts.Add($"{running.Count} games running");
        }

        var active = snapshot.Queue.Jobs.Where(IsActive).ToList();
        if (active.Count > 0)
        {
            parts.Add(QueueLine(snapshot.Queue, active, live));
        }

        var text = parts.Count == 0 ? "Glider" : "Glider · " + string.Join(" · ", parts);
        return text.Length <= MaxTooltip ? text : text[..(MaxTooltip - 1)] + "…";
    }
}
