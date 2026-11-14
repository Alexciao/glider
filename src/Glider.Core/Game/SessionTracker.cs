using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// Per-game session tracking (docs/v0.2-design.md §2.2). Glider spawns the exe from <c>launch --json</c>, then follows the
/// spawned PID <b>and its descendants</b> (one Toolhelp snapshot per poll, only while a session lives) plus the profile's
/// tracked names, which catch processes that re-parent (Fortnite: launcher → bootstrapper → EAC wrapper → shipping, the
/// last one started by the anti-cheat service). A tracked PID stays tracked until it exits, so a chain survives its
/// intermediate stubs exiting. States: Launching → Running (the profile's liveness process appeared, or anything tracked
/// runs when the profile has none) → Closing (nothing tracked left) → Idle after <see cref="EndGrace"/> with nothing back.
/// A launch whose processes all vanish before Running (or never start) fails. <see cref="Tick"/> is the whole state machine.
/// </summary>
internal sealed class SessionTracker
{
    /// <summary>Processes started this much before the session (clock skew, a stub spawning instantly) still count.</summary>
    private static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(2);

    private readonly IProcessMonitor _monitor;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public SessionTracker(IProcessMonitor monitor, TimeProvider time, TimeSpan launchTimeout, TimeSpan? endGrace = null, TimeSpan? launchGrace = null)
    {
        _monitor = monitor;
        _time = time;
        LaunchTimeout = launchTimeout;
        EndGrace = endGrace ?? TimeSpan.FromSeconds(5);
        LaunchGrace = launchGrace ?? TimeSpan.FromSeconds(10);
    }

    public TimeSpan LaunchTimeout { get; }

    /// <summary>A running session ends when nothing tracked remains for this long.</summary>
    public TimeSpan EndGrace { get; }

    /// <summary>A launch fails when nothing tracked remains for this long before the game is up.</summary>
    public TimeSpan LaunchGrace { get; }

    /// <summary>(app, state, runningSince, launchFailed) on every transition.</summary>
    public event Action<string, RunState, DateTimeOffset?, bool>? Changed;

    public bool AnyActive
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count > 0;
            }
        }
    }

    public bool IsActive(string app)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(app);
        }
    }

    public IReadOnlyList<int> TrackedPids(string app)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(app, out var s) ? [.. s.Tracked] : [];
        }
    }

    /// <summary>Launch requested: Launching until the spawned process (or the profile's game process) shows up.</summary>
    public void Begin(string app, GameProfile profile)
    {
        lock (_gate)
        {
            _sessions[app] = new Session(app, profile, _time.GetUtcNow(), Phase.Launching);
        }
    }

    /// <summary>The exe Legendary resolved was started: its PID roots the tracked tree.</summary>
    public void Spawned(string app, RunningProcess process)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(app, out var s))
            {
                s.Root = process.Id;
                s.Tracked.Add(process.Id);
            }
        }
    }

    /// <summary>A launch that never started a process.</summary>
    public void Abort(string app)
    {
        lock (_gate)
        {
            _sessions.Remove(app);
        }
    }

    /// <summary>
    /// Startup / pre-launch: the profile's game process already runs (started by EGL, or before Glider). Adopts it as a
    /// Running session. Only games with a profile liveness process can be detected this way.
    /// </summary>
    public bool DetectExisting(string app, GameProfile profile)
    {
        if (profile.LivenessProcessName is not { } liveness || IsActive(app))
        {
            return false;
        }

        var found = _monitor.Find(liveness);
        if (found.Count == 0)
        {
            return false;
        }

        var since = found[0].StartTime ?? _time.GetUtcNow();
        lock (_gate)
        {
            var s = new Session(app, profile, DateTimeOffset.MinValue, Phase.Running) { RunningSince = since };
            foreach (var p in found)
            {
                s.Tracked.Add(p.Id);
            }

            _sessions[app] = s;
        }

        Changed?.Invoke(app, RunState.Running, since, false);
        return true;
    }

    /// <summary>One poll over every session (one process snapshot). Returns false once no session is left (stop polling).</summary>
    public bool Tick()
    {
        List<Session> sessions;
        lock (_gate)
        {
            if (_sessions.Count == 0)
            {
                return false;
            }

            sessions = [.. _sessions.Values];
        }

        var snapshot = _monitor.Snapshot();
        var byPid = new Dictionary<int, ProcessEntry>();
        var children = new Dictionary<int, List<ProcessEntry>>();
        foreach (var p in snapshot)
        {
            byPid[p.Id] = p;
            if (!children.TryGetValue(p.ParentId, out var list))
            {
                children[p.ParentId] = list = [];
            }

            list.Add(p);
        }

        var now = _time.GetUtcNow();
        var transitions = new List<(string App, RunState State, DateTimeOffset? Since, bool Failed)>();
        lock (_gate)
        {
            foreach (var s in sessions)
            {
                if (!_sessions.ContainsKey(s.App))
                {
                    continue;
                }

                Grow(s, byPid, children);
                s.Tracked.RemoveWhere(pid => !byPid.ContainsKey(pid));
                var alive = s.Tracked.Select(pid => byPid[pid]).ToList();
                var live = s.Profile.LivenessProcessName is { } liveness
                    ? alive.FirstOrDefault(p => string.Equals(p.Name, liveness, StringComparison.OrdinalIgnoreCase))
                    : alive.FirstOrDefault();
                s.EmptySince = alive.Count > 0 ? null : s.EmptySince ?? now;

                switch (s.Phase)
                {
                    case Phase.Launching when live is not null:
                    case Phase.Launching when alive.Count > 0 && now - s.StartedAt > LaunchTimeout:
                        // Up (or something of the game still runs past the timeout: track it rather than lose it).
                        s.Phase = Phase.Running;
                        s.RunningSince = StartOf(live ?? alive[0]) ?? now;
                        transitions.Add((s.App, RunState.Running, s.RunningSince, false));
                        break;

                    case Phase.Launching when s.EmptySince is { } gone && (now - gone >= LaunchGrace || now - s.StartedAt > LaunchTimeout):
                        _sessions.Remove(s.App);
                        transitions.Add((s.App, RunState.Idle, null, true));
                        break;

                    case Phase.Running when alive.Count == 0:
                        s.Phase = Phase.Closing;
                        transitions.Add((s.App, RunState.Closing, null, false));
                        break;

                    case Phase.Closing when alive.Count > 0:
                        s.Phase = Phase.Running;
                        transitions.Add((s.App, RunState.Running, s.RunningSince, false));
                        break;

                    case Phase.Closing when s.EmptySince is { } gone && now - gone >= EndGrace:
                        _sessions.Remove(s.App);
                        transitions.Add((s.App, RunState.Idle, null, false));
                        break;
                }
            }
        }

        foreach (var t in transitions)
        {
            Changed?.Invoke(t.App, t.State, t.Since, t.Failed);
        }

        lock (_gate)
        {
            return _sessions.Count > 0;
        }
    }

    /// <summary>Adds descendants of tracked PIDs and the profile's named processes that started with (or after) the session.</summary>
    private void Grow(Session s, Dictionary<int, ProcessEntry> byPid, Dictionary<int, List<ProcessEntry>> children)
    {
        var queue = new Queue<int>(s.Tracked.Where(byPid.ContainsKey));
        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (!children.TryGetValue(pid, out var kids))
            {
                continue;
            }

            foreach (var child in kids)
            {
                if (child.Id != pid && !s.Tracked.Contains(child.Id) && StartedWithSession(s, child))
                {
                    s.Tracked.Add(child.Id);
                    queue.Enqueue(child.Id);
                }
            }
        }

        var names = s.Profile.AllTrackedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            return;
        }

        foreach (var p in byPid.Values)
        {
            if (!s.Tracked.Contains(p.Id) && names.Contains(p.Name) && StartedWithSession(s, p))
            {
                s.Tracked.Add(p.Id);
            }
        }
    }

    /// <summary>Not an older, unrelated process that happens to reuse a PID or a name.</summary>
    private bool StartedWithSession(Session s, ProcessEntry p)
    {
        if (s.StartedAt == DateTimeOffset.MinValue)
        {
            return true; // adopted session: anything of the game counts
        }

        var start = StartOf(p);
        return start is null || start >= s.StartedAt - StartSlack;
    }

    private DateTimeOffset? StartOf(ProcessEntry p) => p.StartTime ?? _monitor.StartTimeOf(p.Id);

    /// <summary>Poll until no session is left. Each iteration is one snapshot — no handles are held between polls.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        do
        {
            await Task.Delay(interval, _time, ct);
        }
        while (Tick());
    }

    private enum Phase
    {
        Launching,
        Running,
        Closing,
    }

    private sealed class Session(string app, GameProfile profile, DateTimeOffset startedAt, Phase phase)
    {
        public string App { get; } = app;
        public GameProfile Profile { get; } = profile;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public Phase Phase { get; set; } = phase;
        public int? Root { get; set; }
        public HashSet<int> Tracked { get; } = [];
        public DateTimeOffset? EmptySince { get; set; }
        public DateTimeOffset? RunningSince { get; set; }
    }
}
