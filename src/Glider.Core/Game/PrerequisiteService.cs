using System.Collections.Immutable;
using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// Game prerequisites (L§5): the redistributables a manifest asks to run once (<c>installed.json prereq_info</c>: ids, name, path,
/// args). Legendary never runs them non-interactively and <c>launch</c> never looks at them, so Glider does: elevated through the host
/// (one UAC prompt), just before a game's first launch (<see cref="RunState.InstallingPrerequisites"/>) or on demand
/// (<c>game.installPrerequisites</c>). Ids that ran are remembered in <c>state/prereqs.json</c> across games and updates, so a shared
/// redistributable (the same VC++ runtime) runs once. A failure never blocks the game for good: the launch error offers
/// "play anyway", and choosing it stops the question until the user asks for the install themselves.
/// </summary>
internal sealed class PrerequisiteService : IDisposable
{
    private const string Category = "prereq";

    /// <summary>Installer exit codes that mean "installed": success, reboot required, reboot initiated, a newer version is already present.</summary>
    internal static readonly int[] SuccessCodes = [0, 3010, 1641, 1638];

    private readonly ServiceContext _c;
    private readonly LazyState<PrereqFile> _state;

    public PrerequisiteService(ServiceContext context)
    {
        _c = context;
        _state = new LazyState<PrereqFile>(Path.Combine(context.Paths.State, "prereqs.json"), GameStateJsonContext.Default.PrereqFile, PrereqFile.Empty, context.Log);
    }

    /// <summary>One prerequisite program of an installed game, resolved against its install folder.</summary>
    internal sealed record Declared(string App, string Name, string Exe, string Args, string InstallRoot, ImmutableArray<string> Ids);

    public void Dispose() => _state.Dispose();

    /// <summary>The game's declared prerequisite program, or null when it has none (or isn't installed). Nothing is checked on disk here.</summary>
    internal Declared? Declare(string app)
    {
        if (_c.Library.Installed(app) is not { Prerequisites: { Path: { Length: > 0 } relative } info } installed || string.IsNullOrEmpty(installed.InstallPath))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(installed.InstallPath);
        var exe = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
        var ids = info.Ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (ids.IsEmpty)
        {
            ids = [$"app:{app}:{relative.ToLowerInvariant()}"]; // no ids in the manifest: remember it per game
        }

        return new Declared(app, info.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(exe), exe, info.Args ?? "", root, ids);
    }

    /// <summary><see cref="GameDetail.Prerequisites"/>: one entry per game with the ids it asks for; Installed once all ran.</summary>
    public async Task<IReadOnlyList<Prerequisite>> ForAsync(string app, CancellationToken ct)
    {
        if (Declare(app) is not { } d)
        {
            return [];
        }

        var file = await _state.GetAsync(ct);
        return [new Prerequisite(string.Join(',', d.Ids), d.Name, d.Ids.All(file.Ids.ContainsKey))];
    }

    /// <summary>True when the game declares a program whose ids haven't all run and the user hasn't chosen to play without it.</summary>
    public async Task<bool> IsPendingAsync(string app, CancellationToken ct)
    {
        if (Declare(app) is not { } d)
        {
            return false;
        }

        var file = await _state.GetAsync(ct);
        return d.Ids.Any(id => !file.Ids.ContainsKey(id) && !file.Skipped.ContainsKey(id));
    }

    /// <summary>Launch path: runs the pending program (UAC) before the first start. Returns true if it ran now. Throws PrerequisiteFailed.</summary>
    public async Task<bool> EnsureAsync(string app, Action onInstalling, CancellationToken ct)
    {
        if (!await IsPendingAsync(app, ct))
        {
            return false;
        }

        await RunAsync(Declare(app)!, onInstalling, ct);
        return true;
    }

    /// <summary>"Play anyway": remember not to ask again for these ids until the user installs them on purpose.</summary>
    public async Task SkipAsync(string app, CancellationToken ct)
    {
        if (Declare(app) is not { } d)
        {
            return;
        }

        var now = _c.Now;
        await _state.UpdateAsync(f => d.Ids.All(f.Skipped.ContainsKey) ? f : f with { Skipped = f.Skipped.SetItems(d.Ids.Select(i => KeyValuePair.Create(i, now))) }, ct);
        _c.Log.Info(Category, $"{app}: playing without prerequisites \"{d.Name}\" (not asking again)");
    }

    /// <summary><c>game.installPrerequisites</c>: runs the game's program now, even if it ran before (repairing a redistributable).</summary>
    public async Task InstallAsync(string app, CancellationToken ct)
    {
        var summary = _c.Library.Summary(app) ?? throw new GliderException(Errors.NotFound(app));
        if (Declare(app) is not { } d)
        {
            if (summary.InstallStatus is not (InstallStatus.Installed or InstallStatus.Broken))
            {
                throw new GliderException(Errors.NotInstalled(summary.Title, app));
            }

            return; // the game declares no prerequisites: nothing to do
        }

        if (_c.Hub.State.RunStateOf(app) != RunState.Idle || _c.IsGameRunning(app))
        {
            throw new GliderException(Errors.GameRunning(summary.Title, app));
        }

        var title = summary.Title;
        _c.Hub.Mutate(s => s with { Sessions = s.Sessions.SetItem(app, new RunningGame(app, title, RunState.InstallingPrerequisites, null, false, false)) });
        try
        {
            await RunAsync(d, () => { }, ct);
        }
        finally
        {
            _c.Hub.Mutate(s => s.Sessions.ContainsKey(app) ? s with { Sessions = s.Sessions.Remove(app) } : s);
        }
    }

    private async Task RunAsync(Declared d, Action onInstalling, CancellationToken ct)
    {
        var title = _c.TitleOf(d.App);
        GliderException Fail(string detail) => new(Errors.PrerequisiteFailed(d.Name, detail, title, d.App));

        // The path comes from Epic's manifest, but the file sits in a folder any program of this user can write: it must stay
        // inside the game folder, exist and carry a valid signature, or Glider would lend its UAC prompt to a swapped file.
        var rootWithSep = d.InstallRoot + Path.DirectorySeparatorChar;
        if (!d.Exe.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail($"The installer path points outside the game folder ({d.Exe}).");
        }

        if (!File.Exists(d.Exe))
        {
            throw Fail($"The installer isn't in the game folder ({Path.GetFileName(d.Exe)}). Repair the game to restore it.");
        }

        if (_c.Options.PrerequisiteCheck.WhyUntrusted(d.Exe) is { } why)
        {
            _c.Log.Error(Category, $"Not running {d.Exe} elevated: {why}.");
            throw Fail($"Glider won't run {Path.GetFileName(d.Exe)} as administrator because {why}. Repair the game to restore it.");
        }

        onInstalling();
        _c.Log.Info(Category, $"{d.App}: installing \"{d.Name}\" ({Path.GetFileName(d.Exe)} {d.Args})");
        var exit = await _c.Host.RunElevatedAsync(d.Exe, d.Args, ct);
        if (exit is null)
        {
            throw Fail("The Windows permission prompt was declined.");
        }

        if (!SuccessCodes.Contains(exit.Value))
        {
            throw Fail($"{Path.GetFileName(d.Exe)} exited with code {exit}.");
        }

        var now = _c.Now;
        var code = exit.Value;
        await _state.UpdateAsync(f => f with
        {
            Ids = f.Ids.SetItems(d.Ids.Select(i => KeyValuePair.Create(i, new PrereqDone(now, d.App, code)))),
            Skipped = f.Skipped.RemoveRange(d.Ids),
        }, ct);
        _c.Log.Info(Category, $"{d.App}: \"{d.Name}\" installed (exit {code})");
    }
}
