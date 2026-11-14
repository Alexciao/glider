using Glider.Core.Contracts;
using Glider.Core.Links;
using Glider.Core.Services;

namespace Glider.Core.Shortcuts;

/// <summary>File names and link arguments of the shortcuts Glider creates. Pure, shared by the service and the uninstall cleanup.</summary>
public static class ShortcutNames
{
    private const int MaxTitleLength = 80;

    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>The arguments of a Glider shortcut: handled by the single-instance pipe (running Glider) or the cold start (a new one).</summary>
    public static string LaunchArguments(string appName) => "--launch " + appName;

    /// <summary>The app a Glider shortcut launches, or null when the arguments aren't ours.</summary>
    public static string? AppOf(string? arguments)
    {
        const string prefix = "--launch ";
        if (arguments is null || !arguments.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var app = arguments[prefix.Length..].Trim();
        return GliderLinkParser.IsValidAppName(app) ? app : null;
    }

    /// <summary>A title made safe as a file name (no reserved characters or device names, no trailing dots/spaces), falling back to the app name.</summary>
    public static string FileName(string? title, string appName)
    {
        var chars = (title ?? "").Select(c => c < 0x20 || "<>:\"/\\|?*".Contains(c) ? ' ' : c).ToArray();
        var name = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > MaxTitleLength)
        {
            name = name[..MaxTitleLength];
        }

        name = name.TrimEnd('.', ' ').TrimStart();
        if (name.Length == 0 || Reserved.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
        {
            return appName;
        }

        return name;
    }

    /// <summary>
    /// File names to try in order: the plain title (what Epic's launcher names its shortcuts), then "(Glider)" variants when a shortcut by
    /// that name belongs to something else, so Glider never overwrites a link it didn't create.
    /// </summary>
    public static IEnumerable<string> Candidates(string? title, string appName)
    {
        var name = FileName(title, appName);
        yield return name + ".lnk";
        yield return name + " (Glider).lnk";
        for (var i = 2; i <= 5; i++)
        {
            yield return $"{name} (Glider {i}).lnk";
        }
    }
}

/// <summary>
/// Per-game Desktop and Start-menu shortcuts. A shortcut starts Glider (never the game) with <c>--launch &lt;app&gt;</c>: a running Glider
/// launches at once through the single-instance pipe, a cold start launches without showing the window. The icon is the game's exe.
/// Shortcuts are tracked in games.json (<see cref="ShortcutState"/>) and repaired after a move, removed on uninstall, forgotten when
/// the user deletes the file.
/// </summary>
internal sealed class ShortcutService : IDisposable
{
    private static readonly TimeSpan ReconcileDelay = TimeSpan.FromMilliseconds(750);

    private readonly ServiceContext _c;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private ITimer? _timer;
    private bool _disposed;

    public ShortcutService(ServiceContext context) => _c = context;

    private IShellIntegration? Shell => _c.Options.Shell;

    /// <summary>Reacts to install-record changes (move, uninstall) without polling.</summary>
    public void Attach()
    {
        if (Shell is null || _timer is not null)
        {
            return;
        }

        _timer = _c.Time.CreateTimer(_ => _ = ReconcileAllAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _c.Games.Changed += OnGamesChanged;
    }

    private void OnGamesChanged()
    {
        if (!_disposed)
        {
            _timer?.Change(ReconcileDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _c.Games.Changed -= OnGamesChanged;
        _timer?.Dispose();
        _gate.Dispose();
    }

    // ---------------------------------------------------------------- game.setShortcut

    public async Task<ShortcutState> SetAsync(ShortcutRequest request)
    {
        var shell = Shell ?? throw new GliderException(Errors.NotAvailableYet("Shortcuts aren't available in this build"));
        var app = request.AppName;
        if (!GliderLinkParser.IsValidAppName(app) || !Enum.IsDefined(request.Location))
        {
            throw new GliderException(Errors.InvalidRequest("Unknown game or shortcut location"));
        }

        var summary = _c.Library.Summary(app) ?? throw new GliderException(Errors.NotFound(app));
        if (summary.Kind == GameKind.Dlc)
        {
            throw new GliderException(Errors.InvalidRequest("DLC doesn't get shortcuts; use the base game"));
        }

        await _gate.WaitAsync(_c.Shutdown).ConfigureAwait(false);
        try
        {
            var folder = FolderOf(shell, request.Location)
                         ?? throw new GliderException(Errors.InvalidRequest("Windows has no such folder for this account"));
            var title = summary.Title;
            if (request.Create)
            {
                if (Installed(app) != InstallKnowledge.Yes)
                {
                    throw new GliderException(Errors.NotInstalled(title, app));
                }

                Write(shell, folder, title, app);
            }
            else
            {
                RemoveOurs(shell, folder, title, app);
            }

            var state = StateOf(shell, title, app);
            await _c.Games.UpdateAsync(app, e => e with { Shortcuts = state }).ConfigureAwait(false);
            _seen[app] = SignatureOf(app);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- repair

    /// <summary>Checks every game that has a shortcut; only games whose install path changed since the last check are touched.</summary>
    public async Task ReconcileAllAsync()
    {
        if (Shell is not { } shell || _disposed)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(_c.Shutdown).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            foreach (var (app, entry) in _c.Games.Current)
            {
                if ((!entry.Shortcuts.Desktop && !entry.Shortcuts.StartMenu) || _seen.TryGetValue(app, out var sig) && sig == SignatureOf(app))
                {
                    continue;
                }

                try
                {
                    await ReconcileAsync(shell, app).ConfigureAwait(false);
                    _seen[app] = SignatureOf(app);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    _c.Log.Warn("shortcuts", $"could not repair the shortcuts of {app}", e);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconcileAsync(IShellIntegration shell, string app)
    {
        var flags = _c.Games.Get(app).Shortcuts;
        var installed = Installed(app);
        if (installed == InstallKnowledge.Unknown)
        {
            return; // library not read yet: never remove anything on a guess
        }

        var title = _c.TitleOf(app);
        var next = flags;
        foreach (var location in new[] { ShortcutLocation.Desktop, ShortcutLocation.StartMenu })
        {
            var on = location == ShortcutLocation.Desktop ? flags.Desktop : flags.StartMenu;
            if (!on || FolderOf(shell, location) is not { } folder)
            {
                continue;
            }

            var ours = FindOurs(shell, folder, title, app);
            bool keep;
            if (installed == InstallKnowledge.No)
            {
                if (ours is not null)
                {
                    shell.Links.Delete(ours);
                    _c.Log.Info("shortcuts", $"removed the {location} shortcut of {app} (not installed any more)");
                }

                keep = false;
            }
            else if (ours is null)
            {
                keep = false; // the user deleted it: respect that
                _c.Log.Info("shortcuts", $"the {location} shortcut of {app} was deleted by the user");
            }
            else
            {
                var desired = SpecFor(shell, app, title);
                var existing = shell.Links.Read(ours);
                if (existing is null || !Same(existing, desired))
                {
                    shell.Links.Write(ours, desired);
                    _c.Log.Info("shortcuts", $"repaired the {location} shortcut of {app}");
                }

                keep = true;
            }

            next = location == ShortcutLocation.Desktop ? next with { Desktop = keep } : next with { StartMenu = keep };
        }

        if (next != flags)
        {
            await _c.Games.UpdateAsync(app, e => e with { Shortcuts = next }).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- files

    private static string? FolderOf(IShellIntegration shell, ShortcutLocation location) =>
        location == ShortcutLocation.Desktop ? shell.DesktopFolder : shell.StartMenuFolder;

    private static bool Same(ShellLinkSpec a, ShellLinkSpec b) =>
        string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Arguments, b.Arguments, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IconPath, b.IconPath, StringComparison.OrdinalIgnoreCase)
        && a.IconIndex == b.IconIndex;

    /// <summary>The link of ours in the folder (matched by its arguments, never by name alone), or null.</summary>
    private static string? FindOurs(IShellIntegration shell, string folder, string title, string app)
    {
        foreach (var name in ShortcutNames.Candidates(title, app))
        {
            var path = Path.Combine(folder, name);
            if (shell.Links.Exists(path) && string.Equals(shell.Links.Read(path)?.Arguments, ShortcutNames.LaunchArguments(app), StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    private void Write(IShellIntegration shell, string folder, string title, string app)
    {
        var path = FindOurs(shell, folder, title, app);
        if (path is null)
        {
            foreach (var name in ShortcutNames.Candidates(title, app))
            {
                var candidate = Path.Combine(folder, name);
                if (!shell.Links.Exists(candidate))
                {
                    path = candidate;
                    break;
                }
            }
        }

        if (path is null)
        {
            throw new GliderException(Errors.InvalidRequest("A different shortcut with this name already exists in that folder"));
        }

        try
        {
            Directory.CreateDirectory(folder);
            shell.Links.Write(path, SpecFor(shell, app, title));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            _c.Log.Warn("shortcuts", $"could not write {path}", e);
            throw new GliderException(Errors.PermissionDenied(e.Message), e);
        }
    }

    private static void RemoveOurs(IShellIntegration shell, string folder, string title, string app)
    {
        foreach (var name in ShortcutNames.Candidates(title, app))
        {
            var path = Path.Combine(folder, name);
            if (shell.Links.Exists(path) && string.Equals(shell.Links.Read(path)?.Arguments, ShortcutNames.LaunchArguments(app), StringComparison.OrdinalIgnoreCase))
            {
                shell.Links.Delete(path);
            }
        }
    }

    private static ShortcutState StateOf(IShellIntegration shell, string title, string app) => new(
        shell.DesktopFolder is { } d && FindOurs(shell, d, title, app) is not null,
        shell.StartMenuFolder is { } s && FindOurs(shell, s, title, app) is not null);

    private ShellLinkSpec SpecFor(IShellIntegration shell, string app, string title)
    {
        var icon = GameExecutable(app) is { } exe && File.Exists(exe) ? exe : shell.LaunchExePath;
        return new ShellLinkSpec(
            shell.LaunchExePath,
            ShortcutNames.LaunchArguments(app),
            Path.GetDirectoryName(shell.LaunchExePath) ?? "",
            icon,
            0,
            $"Play {title}");
    }

    /// <summary>Full path of the installed game's exe (Legendary's installed.json executable), used only for the icon.</summary>
    private string? GameExecutable(string app)
    {
        var root = InstallPath(app);
        var exe = _c.Library.Installed(app)?.Executable;
        return root is null || string.IsNullOrWhiteSpace(exe) || Path.IsPathRooted(exe) || exe.Split('/', '\\').Contains("..")
            ? null
            : Path.Combine(root, exe.Replace('/', Path.DirectorySeparatorChar));
    }

    private string? InstallPath(string app) => _c.Games.Get(app).Install?.InstallPath ?? _c.Library.Summary(app)?.InstallPath;

    private string SignatureOf(string app) => (InstallPath(app) ?? "") + "|" + Installed(app);

    private enum InstallKnowledge { Unknown, Yes, No }

    /// <summary>Glider's record or the library says installed → yes; both say no only counts once Legendary's files were read.</summary>
    private InstallKnowledge Installed(string app)
    {
        if (_c.Games.Get(app).Install is not null)
        {
            return InstallKnowledge.Yes;
        }

        var status = _c.Library.Summary(app)?.InstallStatus;
        if (status is InstallStatus.Installed or InstallStatus.Broken)
        {
            return InstallKnowledge.Yes;
        }

        return _c.Library.FilesRead && status is not null ? InstallKnowledge.No : InstallKnowledge.Unknown;
    }
}

/// <summary>Cleanup of the shortcuts Glider created when Glider itself is uninstalled (no running service needed).</summary>
public static class ShortcutCleanup
{
    /// <summary>
    /// Removes every shortcut Glider created (found by their arguments and Glider's exe name, not by file name) from the given
    /// folders. Shortcuts of other programs are never touched. Returns the number of files deleted.
    /// </summary>
    public static int RemoveAllOurs(IShellLinks links, string exePath, params string?[] folders)
    {
        var exeName = Path.GetFileName(exePath);
        var removed = 0;
        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder))
            {
                continue;
            }

            foreach (var path in links.Enumerate(folder))
            {
                var spec = links.Read(path);
                if (spec is not null && ShortcutNames.AppOf(spec.Arguments) is not null
                    && string.Equals(Path.GetFileName(spec.Target), exeName, StringComparison.OrdinalIgnoreCase) && links.Delete(path))
                {
                    removed++;
                }
            }
        }

        return removed;
    }
}
