using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Infrastructure;
using Glider.Core.Jobs;
using Glider.Core.Services;
using Glider.Core.Tests.Services;

namespace Glider.Core.Tests.Contracts;

/// <summary>
/// Core's half of the mock &lt;-&gt; Core conformance suite (review v0.2 M7). tests/conformance/scenarios.json is the table both sides read;
/// the mock's half is frontend/src/ipc/mock/conformance.test.ts. Each scenario drives JSON requests through <see cref="RpcDispatcher"/> into a
/// <see cref="GliderService"/> over the real <see cref="JobQueue"/> (Legendary replaced by a double whose runs hold until the scenario
/// ends or fails them) and checks the results, the queue and the error codes. The table's header explains the steps and the worlds.
/// </summary>
public class ConformanceTests
{
    private static string TablePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Glider.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "conformance", "scenarios.json");
            }
        }

        throw new InvalidOperationException("repo root not found");
    }

    private static JsonArray Scenarios() => JsonNode.Parse(File.ReadAllText(TablePath()))!["scenarios"]!.AsArray();

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var s in Scenarios())
        {
            data.Add(s!["name"]!.GetValue<string>());
        }

        return data;
    }

    [Fact]
    public void TheTableHasScenarios() => Assert.True(Scenarios().Count > 10);

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(Names))]
    public async Task Scenario(string name)
    {
        var scenario = Scenarios().Single(s => s!["name"]!.GetValue<string>() == name)!;
        await using var world = await World.StartAsync(scenario["world"]!.GetValue<string>(), TestContext.Current.CancellationToken);
        var steps = scenario["steps"]!.AsArray();
        for (var i = 0; i < steps.Count; i++)
        {
            await world.RunStepAsync(steps[i]!.AsObject(), $"step {i + 1} ({steps[i]!["call"] ?? steps[i]!["env"]})");
        }
    }

    // ------------------------------------------------------------------ the doubles

    /// <summary>Legendary whose writer-lane runs (install, verify, move, uninstall, import) hold until the scenario fails them or the job is stopped.</summary>
    private sealed class HeldLegendary(Glider.Core.Tests.Services.FakeLegendary inner, WriterActivity activity) : ILegendary
    {
        private readonly List<TaskCompletionSource<LegendaryOutcome>> _held = [];

        /// <summary>Fails the run the lane is in (waiting a moment for it to reach Legendary: the job reads "running" a little before that).</summary>
        public async Task FailRunningAsync(GliderError error)
        {
            for (var i = 0; i < 250; i++)
            {
                lock (_held)
                {
                    if (_held.LastOrDefault() is { } run)
                    {
                        run.TrySetResult(new LegendaryOutcome(false, error, null, null));
                        return;
                    }
                }

                await Task.Delay(20);
            }

            throw new InvalidOperationException("no run to fail");
        }

        private async Task<LegendaryOutcome> HoldAsync(CancellationToken ct)
        {
            // A held run stands for a download that is under way: it is past the pre-download phase that blocks the metadata lane.
            activity.EndPreDownload();
            var run = new TaskCompletionSource<LegendaryOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_held)
            {
                _held.Add(run);
            }

            try
            {
                return await run.Task.WaitAsync(ct);
            }
            finally
            {
                lock (_held)
                {
                    _held.Remove(run);
                }
            }
        }

        public Task<LegendaryOutcome> InstallAsync(InstallArgs args, IProgress<LegendaryProgress> progress, CancellationToken ct) => HoldAsync(ct);

        public Task<LegendaryOutcome> VerifyAsync(string appName, IProgress<LegendaryProgress> progress, CancellationToken ct) => HoldAsync(ct);

        public Task<LegendaryOutcome> ImportAsync(string appName, string gamePath, CancellationToken ct) => HoldAsync(ct);

        public Task<LegendaryOutcome> MoveAsync(string appName, string newBasePath, bool skipMove, IProgress<LegendaryProgress>? progress, CancellationToken ct) => HoldAsync(ct);

        public Task<LegendaryOutcome> UninstallAsync(string appName, bool keepFiles, CancellationToken ct) => HoldAsync(ct);

        public Task<string> GetVersionAsync(CancellationToken ct) => inner.GetVersionAsync(ct);

        public Task<LegendaryAccount?> GetAccountOfflineAsync(CancellationToken ct) => inner.GetAccountOfflineAsync(ct);

        public Task<LegendaryAccount?> RefreshSessionAsync(CancellationToken ct) => inner.RefreshSessionAsync(ct);

        public Task<LegendaryAccount> SignInWithExchangeTokenAsync(string exchangeToken, CancellationToken ct) => inner.SignInWithExchangeTokenAsync(exchangeToken, ct);

        public Task SignOutAsync(CancellationToken ct) => inner.SignOutAsync(ct);

        public Task<LegendaryGameInfo> GetGameInfoAsync(string appName, CancellationToken ct) => inner.GetGameInfoAsync(appName, ct);

        public Task<LegendaryInstalledGame?> GetInstalledAsync(string appName, CancellationToken ct) => inner.GetInstalledAsync(appName, ct);

        public Task<LaunchCommand> GetLaunchCommandAsync(string appName, bool skipVersionCheck, string extraArgs, bool offline, CancellationToken ct) =>
            inner.GetLaunchCommandAsync(appName, skipVersionCheck, extraArgs, offline, ct);

        public string? GetInstalledManifestPath(string appName, string version) => inner.GetInstalledManifestPath(appName, version);

        public Task<LegendarySizes?> GetUpdateSizesAsync(string appName, CancellationToken ct) => inner.GetUpdateSizesAsync(appName, ct);

        public Task<LegendaryOutcome> RefreshLibraryAsync(CancellationToken ct) => inner.RefreshLibraryAsync(ct);

        public Task<LegendaryOutcome> CheckUpdatesAsync(CancellationToken ct) => inner.CheckUpdatesAsync(ct);

        public Task<LegendaryOutcome> InstallOverlayAsync(string? path, IProgress<LegendaryProgress> progress, CancellationToken ct) => HoldAsync(ct);

        public Task<LegendaryOutcome> RemoveOverlayAsync(CancellationToken ct) => inner.RemoveOverlayAsync(ct);

        public Task<LegendaryOutcome> ActivateUplayAsync(CancellationToken ct) => inner.ActivateUplayAsync(ct);

        public Task<string> GetThirdPartyUriAsync(string appName, ThirdPartyStore store, bool offline, CancellationToken ct) => inner.GetThirdPartyUriAsync(appName, store, offline, ct);

        public Task<string?> GetWebExchangeCodeAsync(CancellationToken ct) => inner.GetWebExchangeCodeAsync(ct);
    }

    /// <summary>The fake EGL, busy only with the folders a scenario names (the stock fake is busy with every path or none).</summary>
    private sealed class SelectiveEgl(FakeEgl inner) : IEglService
    {
        private readonly ConcurrentDictionary<string, bool> _busy = new(StringComparer.OrdinalIgnoreCase);

        public void SetBusy(string path, bool busy)
        {
            var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (busy)
            {
                _busy[key] = true;
            }
            else
            {
                _busy.TryRemove(key, out _);
            }
        }

        public bool IsBusyWith(string installPath) => _busy.ContainsKey(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath)));

        public Task<EglScanResult> ScanAsync(CancellationToken ct) => inner.ScanAsync(ct);

        public IReadOnlyList<EglFoundInstall> QuickScan(IReadOnlyList<string> driveRoots, CancellationToken ct) => inner.QuickScan(driveRoots, ct);

        public bool IsLauncherRunning() => inner.IsLauncherRunning();

        public Task WaitUntilNotBusyAsync(string installPath, Action<string?> onBlocked, CancellationToken ct) => Task.CompletedTask;

        public EglItem? FindByLocation(string installPath, string? appName = null) => inner.FindByLocation(installPath, appName);

        public DriftResult CheckDrift(InstallRecord record) => inner.CheckDrift(record);

        public Task<string> WriteBackAsync(EglWriteBack data, CancellationToken ct) => inner.WriteBackAsync(data, ct);

        public Task UpdateLocationAsync(string appName, string installationGuid, string newInstallPath, CancellationToken ct) =>
            inner.UpdateLocationAsync(appName, installationGuid, newInstallPath, ct);

        public string? DetectAppName(string folder) => inner.DetectAppName(folder);
    }

    private sealed class Capture : IRpcTransport
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        public void Send(string json) => Sent.Enqueue(json);
    }

    // ------------------------------------------------------------------ the world

    private sealed class World : IAsyncDisposable
    {
        private static readonly TimeSpan PollFor = TimeSpan.FromSeconds(8);

        private readonly Rig _rig;
        private readonly HeldLegendary _legendary;
        private readonly SelectiveEgl _egl;
        private readonly JobQueue _jobs;
        private readonly GliderService _service;
        private readonly Capture _wire = new();
        private readonly RpcDispatcher _rpc;
        private readonly Dictionary<string, string> _roles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _saved = new(StringComparer.Ordinal);
        private int _nextId;
        private CancellationToken _ct;

        private World(Rig rig)
        {
            _rig = rig;
            var activity = new WriterActivity();
            _legendary = new HeldLegendary(rig.Legendary, activity);
            _egl = new SelectiveEgl(rig.Egl);
            var hooks = new DeferredJobHooks();
            var policy = new DeferredQueuePolicy();
            GliderService? service = null;
            _jobs = new JobQueue(rig.Paths, _legendary, rig.Settings, hooks, rig.Log, TimeProvider.System, new JobQueueOptions
            {
                AutoTune = new AutoTuneResult(7, 1536),
                Policy = policy,
                Activity = activity,
                ProgressInterval = TimeSpan.FromMilliseconds(5),
                VerifyAfterInstall = VerifyPolicy.Never,
                VerifyAfterUpdate = VerifyPolicy.Never,
                GateRecheckInterval = TimeSpan.FromMilliseconds(40),
                StopTimeout = TimeSpan.FromSeconds(10),
                FolderNameFor = app => service?.LibraryForTests.FolderName(app),
            });
            service = new GliderService(
                rig.Paths, rig.Host, _legendary, _jobs, _egl, rig.Settings, rig.Games, rig.Log, rig.Time, rig.Sink,
                new GliderServiceOptions
                {
                    ProcessMonitor = rig.Processes,
                    ProcessStarter = rig.Processes,
                    ServiceQuery = rig.Services,
                    DiskInfo = rig.Disk,
                    PublisherCheck = rig.Publisher,
                    PrerequisiteCheck = rig.PrerequisiteCheck,
                    OverlayRegistry = rig.OverlayRegistry,
                    OverlayEnvironment = new Glider.Core.Overlay.OverlayEnvironment(rig.ProgramFilesX86),
                    ThirdPartyLaunchers = rig.Launchers,
                    ThirdPartyHandoffDisplay = TimeSpan.FromSeconds(8),
                    OnboardingStore = rig.Onboarding,
                    ArtFetcher = rig.ArtFetcher,
                    ProcessPollInterval = TimeSpan.FromHours(1), // a started game stays "launching"; nothing ends its session by itself
                    LibraryCoalesceInterval = TimeSpan.Zero,
                    ConnectivityProbe = rig.Probe,
                    NetworkChangeSignal = rig.NetworkSignal,
                    LibraryCacheDelay = TimeSpan.Zero,
                    LibraryRetryDelay = TimeSpan.FromMilliseconds(5),
                    AppVersion = "0.2.0-test",
                    LicensesDirectory = Path.Combine(rig.Root, "licenses"),
                    WriterActivity = activity,
                });
            _service = service;
            hooks.Target = service.Hooks;
            policy.Target = service.QueuePolicy;
            _rpc = new RpcDispatcher(service, _wire);
        }

        public static async Task<World> StartAsync(string world, CancellationToken ct)
        {
            var rig = new Rig(latest: "v2");
            var w = new World(rig) { _ct = ct };
            switch (world)
            {
                case "library":
                    w.BuildLibrary();
                    break;
                case "egl":
                    w.BuildEgl();
                    break;
                case "onboarding":
                    break;
                default:
                    throw new InvalidOperationException($"unknown world '{world}'");
            }

            rig.SaveLibrary();
            await w._service.StartAsync();
            await w._service.WarmupTask;
            await w._service.PrewarmForTests.LibrarySync;
            await w._service.LibraryForTests.FlushAsync();
            return w;
        }

        /// <summary>a = Fortnite (installed at v1, has an uninstalled DLC x), b = an installed game without cloud saves, c..f = owned, not installed.</summary>
        private void BuildLibrary()
        {
            _rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never }); // a at v1 with v2 out would update by itself
            var rec = _rig.Install("v1");
            _rig.Services.Installed.Add("EasyAntiCheat_EOS");
            var setup = Path.Combine(rec.InstallPath, GameProfiles.Fortnite.Eac!.SetupRelative.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
            File.WriteAllText(setup, "");
            _rig.AddGame("Alien", "Alien: Isolation", "1.0", "1.0");
            foreach (var (app, title) in new[] { ("Castle", "Castlevania"), ("Doom", "Doom"), ("Echo", "Echo Night"), ("Frost", "Frostpunk") })
            {
                _rig.AddGame(app, title, "1.0");
            }

            _rig.AddGame("FnPack", "Fortnite Pack", "1.0", baseApp: FortniteLayout.AppName);
            foreach (var (role, app) in new[] { ("a", FortniteLayout.AppName), ("b", "Alien"), ("c", "Castle"), ("d", "Doom"), ("e", "Echo"), ("f", "Frost"), ("x", "FnPack") })
            {
                _roles[role] = app;
            }
        }

        /// <summary>Fortnite and Alien have EGL installs the scan reports as importable, Castlevania's folder is gone (stale).</summary>
        private void BuildEgl()
        {
            _rig.AddGame("Alien", "Alien: Isolation", "1.0");
            _rig.AddGame("Castle", "Castlevania", "1.0");
            var fortnite = _rig.MakeGameFolder();
            _rig.Legendary.ImportVersion = "v2";
            _rig.Egl.Scan = _rig.Egl.Scan with
            {
                Installs =
                [
                    new EglFoundInstall("egl-a", CandidateSource.EpicGamesLauncher, FortniteLayout.AppName, "Fortnite", fortnite, "v2", 1, true, true, false, "g1"),
                    new EglFoundInstall("egl-c", CandidateSource.EpicGamesLauncher, "Castle", "Castlevania", Path.Combine(_rig.Root, "gone"), "1.0", 1, false, false, false, "g3"),
                ],
            };
        }

        // ------------------------------------------------------------------ steps

        public async Task RunStepAsync(JsonObject step, string where)
        {
            if (step["env"] is { } env)
            {
                await EnvAsync(env.GetValue<string>(), step);
            }
            else
            {
                var method = step["call"]!.GetValue<string>();
                var parameters = await ResolveAsync(step["params"]);
                var (result, error) = await CallAsync(method, parameters);
                if (step["error"] is { } expected)
                {
                    Assert.True(error is not null, $"{where} should fail with {expected} (got {result?.ToJsonString()})");
                    Assert.Equal(expected.GetValue<string>(), error["code"]!.GetValue<string>());
                    if (step["errorTitle"] is { } title)
                    {
                        Assert.Equal(title.GetValue<string>(), error["title"]!.GetValue<string>());
                    }
                }
                else if (step["notError"] is { } not)
                {
                    Assert.True(error is null || error["code"]!.GetValue<string>() != not.GetValue<string>(), $"{where} must not fail with {not}: {error?.ToJsonString()}");
                }
                else
                {
                    Assert.True(error is null, $"{where} failed: {error?.ToJsonString()}");
                }

                if (error is null && step["result"] is { } want)
                {
                    var m = Mismatch(await ResolveAsync(want), result);
                    Assert.True(m is null, $"{where}: {m}");
                }

                if (error is null && step["save"] is { } save)
                {
                    var paths = save is JsonValue v ? new Dictionary<string, string> { [v.GetValue<string>()] = "id" } : save.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
                    foreach (var (label, path) in paths)
                    {
                        _saved[label] = Get(result, path)?.ToString() ?? "";
                    }
                }
            }

            if (step["then"] is { } then)
            {
                await CheckAsync(then.AsObject(), where);
            }
        }

        private async Task EnvAsync(string op, JsonObject step)
        {
            switch (op)
            {
                case "failRunning":
                    await _legendary.FailRunningAsync(step["code"]?.GetValue<string>() switch
                    {
                        "network" => Errors.Network("conformance"),
                        "legendaryFailed" => Errors.LegendaryFailed("conformance"),
                        _ => Errors.Unknown("conformance"),
                    });
                    break;
                case "eglBusy":
                {
                    var app = _roles[step["game"]!.GetValue<string>().TrimStart('@')];
                    var path = _service.LibraryForTests.Summary(app)!.InstallPath!;
                    _egl.SetBusy(path, step["busy"]!.GetValue<bool>());
                    _jobs.Reevaluate(); // a "wake" for the lane; Core has no event for "Epic is done", its own timer looks again
                    break;
                }

                case "startGame":
                {
                    var (_, error) = await CallAsync("game.launch", new JsonObject { ["appName"] = _roles[step["game"]!.GetValue<string>().TrimStart('@')], ["mode"] = null, ["skipSaveSync"] = true });
                    Assert.True(error is null, $"startGame failed: {error?.ToJsonString()}");
                    var since = _rig.Time.GetUtcNow().AddSeconds(1);
                    _rig.Time.Advance(TimeSpan.FromSeconds(2));
                    _rig.Processes.Spawn(FortniteLayout.ShippingProcessName, parentId: 999, start: since);
                    _rig.Processes.Remove("FortniteLauncher");
                    _service.LaunchServiceForTests.Sessions.Tick(); // the poll loop is off; this is what it would report
                    break;
                }

                case "connectivity":
                    _service.Hub.Mutate(s => s with { Connectivity = step["value"]!.GetValue<string>() == "offline" ? Connectivity.Offline : Connectivity.Online });
                    break;
                case "setting":
                {
                    var policy = Enum.Parse<WhilePlayingPolicy>(step["whilePlaying"]!.GetValue<string>(), ignoreCase: true);
                    await _service.UpdateSettings(_rig.Settings.Current with { WhilePlaying = policy });
                    break;
                }

                default:
                    throw new InvalidOperationException($"unknown env op '{op}'");
            }
        }

        private async Task<(JsonNode? Result, JsonNode? Error)> CallAsync(string method, JsonNode? parameters)
        {
            var id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject { ["type"] = "request", ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() };
            await _rpc.HandleAsync(request.ToJsonString());
            foreach (var line in _wire.Sent)
            {
                var msg = JsonNode.Parse(line)!;
                if (msg["type"]?.GetValue<string>() == "response" && msg["id"]?.GetValue<int>() == id)
                {
                    return (msg["result"], msg["error"]);
                }
            }

            throw new InvalidOperationException($"no response to {method}");
        }

        // ------------------------------------------------------------------ parameters and checks

        private async Task<JsonNode?> ResolveAsync(JsonNode? node)
        {
            switch (node)
            {
                case JsonValue v when v.TryGetValue<string>(out var s):
                    return await ResolveStringAsync(s);
                case JsonArray a:
                    var arr = new JsonArray();
                    foreach (var item in a)
                    {
                        arr.Add(await ResolveAsync(item));
                    }

                    return arr;
                case JsonObject o:
                    var obj = new JsonObject();
                    foreach (var (k, v) in o)
                    {
                        obj[k] = await ResolveAsync(v);
                    }

                    return obj;
                default:
                    return node?.DeepClone();
            }
        }

        private async Task<JsonNode?> ResolveStringAsync(string s)
        {
            if (Regex.Match(s, @"^@(\w+)$") is { Success: true } plain)
            {
                var role = plain.Groups[1].Value;
                return role == "base" ? Path.Combine(_rig.Root, "Games") : _roles.TryGetValue(role, out var app) ? app : s;
            }

            if (Regex.Match(s, @"^@(parent|inside|other)\((\w+)\)$") is { Success: true } path)
            {
                var install = await InstallPathOfAsync(_roles[path.Groups[2].Value]);
                return path.Groups[1].Value switch
                {
                    "parent" => Path.GetDirectoryName(install),
                    "inside" => install + "\\inside",
                    _ => "Q:\\Moved",
                };
            }

            if (Regex.Match(s, @"^@egl:(\w+)$") is { Success: true } egl)
            {
                var (snapshot, _) = await CallAsync("app.getSnapshot", null);
                var hit = snapshot!["egl"]!["installs"]!.AsArray().FirstOrDefault(i => i!["status"]!.GetValue<string>() == egl.Groups[1].Value);
                return hit is null ? throw new InvalidOperationException($"no {egl.Groups[1].Value} EGL install") : hit["id"]!.GetValue<string>();
            }

            if (Regex.Match(s, @"^\$(\w+)$") is { Success: true } saved)
            {
                return _saved.TryGetValue(saved.Groups[1].Value, out var id) ? id : throw new InvalidOperationException($"no saved value {saved.Groups[1].Value}");
            }

            return s;
        }

        private async Task<string> InstallPathOfAsync(string app)
        {
            var (delta, _) = await CallAsync("library.get", null);
            return delta!["upserts"]!.AsArray().First(g => g!["appName"]!.GetValue<string>() == app)!["installPath"]!.GetValue<string>();
        }

        private static JsonNode? Get(JsonNode? node, string path) => path.Split('.').Aggregate(node, (n, k) => n is JsonArray a && int.TryParse(k, out var i) ? a[i] : n?[k]);

        /// <summary>Subset match: objects by key, arrays by length and element, {"$in": [..]} = any of, {"$same": label} = a saved value.</summary>
        private string? Mismatch(JsonNode? expected, JsonNode? actual, string path = "result")
        {
            switch (expected)
            {
                case JsonObject o when o.ContainsKey("$in"):
                    return o["$in"]!.AsArray().Any(x => JsonNode.DeepEquals(x, actual)) ? null : $"{path}: {actual?.ToJsonString()} is none of {o["$in"]!.ToJsonString()}";
                case JsonObject o when o.ContainsKey("$same"):
                    return _saved[o["$same"]!.GetValue<string>()] == actual?.ToString() ? null : $"{path}: {actual?.ToJsonString()} is not the saved {o["$same"]}";
                case JsonObject o:
                    if (actual is not JsonObject)
                    {
                        return $"{path}: expected an object, got {actual?.ToJsonString()}";
                    }

                    foreach (var (k, v) in o)
                    {
                        if (Mismatch(v, actual[k], $"{path}.{k}") is { } m)
                        {
                            return m;
                        }
                    }

                    return null;
                case JsonArray a:
                    if (actual is not JsonArray b || a.Count != b.Count)
                    {
                        return $"{path}: expected {a.Count} items, got {actual?.ToJsonString()}";
                    }

                    for (var i = 0; i < a.Count; i++)
                    {
                        if (Mismatch(a[i], b[i], $"{path}[{i}]") is { } m)
                        {
                            return m;
                        }
                    }

                    return null;
                default:
                    return JsonNode.DeepEquals(expected, actual) ? null : $"{path}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}";
            }
        }

        private string Label(JsonNode job)
        {
            var id = job["id"]!.GetValue<string>();
            return $"{_saved.FirstOrDefault(kv => kv.Value == id).Key ?? "?"}:{job["status"]!.GetValue<string>()}";
        }

        private string LabelOf(JsonNode job) => _saved.FirstOrDefault(kv => kv.Value == job["id"]!.GetValue<string>()).Key ?? "?";

        /// <summary>The lane runs on its own threads: look until the expectation holds, then report what was seen last.</summary>
        private async Task CheckAsync(JsonObject then, string where)
        {
            var sw = Stopwatch.StartNew();
            var last = "";
            while (true)
            {
                var (snapshot, _) = await CallAsync("app.getSnapshot", null);
                var queue = snapshot!["queue"]!;
                var all = queue["jobs"]!.AsArray().Select(j => j!).ToList();
                var unfinished = all.Where(j => j["status"]!.GetValue<string>() is "queued" or "running" or "paused" or "waiting").ToList();
                var finished = all.Except(unfinished).Where(j => LabelOf(j) != "?").ToList();
                var problems = new List<string>();
                if (then["jobs"] is JsonArray jobs && !unfinished.Select(Label).SequenceEqual(jobs.Select(j => j!.GetValue<string>())))
                {
                    problems.Add($"jobs [{string.Join(", ", unfinished.Select(Label))}] != {jobs.ToJsonString()}");
                }

                if (then["finished"] is JsonArray fin && !finished.Select(Label).Order().SequenceEqual(fin.Select(j => j!.GetValue<string>()).Order()))
                {
                    problems.Add($"finished [{string.Join(", ", finished.Select(Label))}] != {fin.ToJsonString()}");
                }

                if (then["paused"] is { } paused && queue["paused"]!.GetValue<bool>() != paused.GetValue<bool>())
                {
                    problems.Add($"paused {queue["paused"]} != {paused}");
                }

                foreach (var (label, text) in then["reasons"]?.AsObject() ?? [])
                {
                    var j = all.FirstOrDefault(x => x["id"]!.GetValue<string>() == _saved.GetValueOrDefault(label));
                    if (j?["blockedReason"]?.GetValue<string>()?.Contains(text!.GetValue<string>(), StringComparison.Ordinal) != true)
                    {
                        problems.Add($"{label}'s reason {j?["blockedReason"]?.ToJsonString()} lacks {text}");
                    }
                }

                foreach (var (label, want) in then["cancellable"]?.AsObject() ?? [])
                {
                    var j = all.FirstOrDefault(x => x["id"]!.GetValue<string>() == _saved.GetValueOrDefault(label));
                    if (j?["cancellable"]?.GetValue<bool>() != want!.GetValue<bool>())
                    {
                        problems.Add($"{label}.cancellable {j?["cancellable"]} != {want}");
                    }
                }

                if (problems.Count == 0)
                {
                    return;
                }

                last = string.Join("; ", problems);
                if (sw.Elapsed > PollFor)
                {
                    throw new Xunit.Sdk.XunitException($"{where}: {last}");
                }

                await Task.Delay(20, _ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _service.Dispose();
            await _jobs.DisposeAsync();
            _rig.Dispose();
        }
    }
}
