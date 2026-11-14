using System.Text;
using System.Text.Json.Nodes;
using Glider.Core.Contracts;
using Glider.Core.Saves;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Saves;

internal enum DownloadBehavior
{
    Normal,

    /// <summary>Legendary deleted the local files, the manifest fetch failed, and it still printed "Successfully completed".</summary>
    FailsAfterDelete,

    /// <summary>The process is killed (timeout / shutdown) after it deleted the local files.</summary>
    KilledAfterDelete,

    /// <summary>Only the first file was written before a chunk failed.</summary>
    PartialWrite,
}

/// <summary>
/// An in-memory Epic cloud + the semantics of Legendary's `list-saves` / `sync-saves` as read from 0.21.1's source: a download
/// deletes the local save files first and writes every file with the manifest's time as mtime; an upload names the manifest
/// after the newest local mtime and its PUT is never checked. Failures can be injected.
/// </summary>
internal sealed class FakeCloud : ILegendarySaves
{
    private readonly string _config;
    private readonly object _gate = new();

    public FakeCloud(string config, string app)
    {
        _config = config;
        App = app;
    }

    public string App { get; }

    /// <summary>Where Legendary stores the save folder on first use.</summary>
    public string SavePath { get; set; } = "";

    public SaveFilters Filters { get; set; } = SaveFilters.None;

    /// <summary>Saves in upload order (Legendary's "latest" is the last uploaded).</summary>
    public List<(string Name, Dictionary<string, byte[]> Files)> Saves { get; } = [];

    public List<string> Calls { get; } = [];

    public DownloadBehavior Download { get; set; }

    /// <summary>The PUT fails silently: "Finished uploading savegame." is printed, nothing is stored.</summary>
    public bool DropUploads { get; set; }

    public bool SetupStoresNothing { get; set; }

    public GliderException? ListError { get; set; }

    /// <summary>Called with the call name right before a sync runs (tests assert what exists on disk at that moment).</summary>
    public Action<SyncMode>? BeforeSync { get; set; }

    public TaskCompletionSource? SyncGate { get; set; }

    public int SyncCalls(SyncMode mode) => Calls.Count(c => c == "sync:" + mode);

    public void AddSave(DateTimeOffset at, params (string Path, string Text)[] files) =>
        Saves.Add((CloudManifests.NameFor(at), files.ToDictionary(f => f.Path, f => Encoding.UTF8.GetBytes(f.Text), StringComparer.Ordinal)));

    public Task<CloudSaveSnapshot> ListSavesAsync(string app, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add("list");
            if (ListError is { } e)
            {
                throw e;
            }

            return Task.FromResult(new CloudSaveSnapshot([.. Saves.Select(s => s.Name).OrderBy(n => CloudManifests.TryParse(n))]));
        }
    }

    public async Task<SyncRun> SyncAsync(string app, SyncMode mode, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add("sync:" + mode);
        }

        BeforeSync?.Invoke(mode);
        if (SyncGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        lock (_gate)
        {
            return Run(mode, ct);
        }
    }

    private SyncRun Run(SyncMode mode, CancellationToken ct)
    {
        switch (mode)
        {
            case SyncMode.SetupPath:
                if (!SetupStoresNothing)
                {
                    StoreSavePath(SavePath);
                }

                return new SyncRun(false, false, false, false, false, null, ["Computed save path"]);

            case SyncMode.Download:
            {
                var (name, files) = Saves[^1];
                foreach (var (full, rel) in SaveFolder.Walk(SavePath, ct).ToList())
                {
                    if (Filters.Matches(rel))
                    {
                        File.Delete(full);
                    }
                }

                if (Download == DownloadBehavior.KilledAfterDelete)
                {
                    throw new OperationCanceledException("legendary was killed");
                }

                if (Download == DownloadBehavior.FailsAfterDelete)
                {
                    return new SyncRun(false, false, true, true, false, null, ["Download failed, status code: 500", "Successfully completed savegame download."]);
                }

                var at = CloudManifests.TryParse(name)!.Value;
                var written = 0;
                foreach (var (rel, bytes) in files)
                {
                    var target = Path.Combine(SavePath, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                    File.SetLastWriteTimeUtc(target, at.UtcDateTime);
                    if (Download == DownloadBehavior.PartialWrite && ++written == 1)
                    {
                        return new SyncRun(false, false, true, true, false, null, ["Chunk part for x is missing, file may be corrupted!", "Successfully completed savegame download."]);
                    }
                }

                return new SyncRun(false, false, true, false, false, null, ["Downloading remote savegame...", "Successfully completed savegame download."]);
            }

            default:
            {
                var latest = SaveFolder.LatestMtimeUnfiltered(SavePath, ct);
                var files = SaveFolder.Walk(SavePath, ct).Where(f => Filters.Matches(f.Relative)).ToDictionary(f => f.Relative, f => File.ReadAllBytes(f.Full), StringComparer.Ordinal);
                if (files.Count == 0 || latest is null)
                {
                    return new SyncRun(false, true, false, false, false, null, ["No files to upload."]);
                }

                if (!DropUploads)
                {
                    var t = latest.Value;
                    Saves.RemoveAll(s => s.Name == CloudManifests.NameFor(t)); // same name replaces
                    Saves.Add((CloudManifests.NameFor(new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, TimeSpan.Zero)), files));
                }

                return new SyncRun(true, false, false, false, false, null, ["Uploading local savegame...", "Finished uploading savegame."]);
            }
        }
    }

    /// <summary>installed.json's save_path, the way Legendary writes it.</summary>
    public void StoreSavePath(string path)
    {
        var file = Path.Combine(_config, "installed.json");
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        root[App]!.AsObject()["save_path"] = path;
        File.WriteAllText(file, root.ToJsonString());
    }
}

/// <summary>A game with cloud saves, installed, over the standard service rig.</summary>
internal sealed class SaveRig : IDisposable
{
    public const string App = "Saver";

    public SaveRig(string? include = null, string? exclude = null, SaveOptions? options = null)
    {
        Rig = new Rig();
        SaveDir = Path.Combine(Rig.Root, "saves", "Saver");
        Directory.CreateDirectory(SaveDir);
        Cloud = new FakeCloud(Rig.Paths.LegendaryConfig, App) { SavePath = SaveDir };
        Rig.SavesLegendary = Cloud;
        Rig.SaveOptions = options ?? new SaveOptions { PostExitRetryDelays = [], PreLaunchTimeout = TimeSpan.FromSeconds(30) };

        var attributes = new Dictionary<string, string> { ["CloudSaveFolder"] = "{AppData}/Saver" };
        if (include is not null)
        {
            attributes["CloudIncludeList"] = include;
        }

        if (exclude is not null)
        {
            attributes["CloudExcludeList"] = exclude;
        }

        Rig.Library.Add(App, LibraryFixture.Item("Save Game", "ns-saver", "item-saver", null, attributes), "1.0");
        InstallPath = Rig.MakeOtherGameFolder(App);
        Rig.Legendary.Set(App, Glider.Core.Tests.Services.FakeLegendary.MakeInstalled(InstallPath, "1.0", App, "Game.exe"));
        Rig.Games.Put(App, Rig.Games.Get(App) with { Install = new InstallRecord(App, InstallPath, "1.0", 100, DateTimeOffset.UnixEpoch, false, null, false) });
        Filters = SaveFilters.FromMetadata(include, exclude);
        Cloud.Filters = Filters;
    }

    public Rig Rig { get; }

    public FakeCloud Cloud { get; }

    public string SaveDir { get; }

    public string InstallPath { get; }

    public SaveFilters Filters { get; }

    public Glider.Core.Services.GliderService Service => Rig.Service;

    public SaveService Saves => Rig.Service.SavesForTests;

    public string BackupsDir => Path.Combine(Rig.Paths.Backups, "saves", App);

    /// <summary>Starts the service; the save folder is already stored in installed.json unless <paramref name="pathStored"/> is false.</summary>
    public async Task StartAsync(bool pathStored = true)
    {
        await Rig.StartWarmAsync();
        Rig.Service.Hub.Mutate(s => s with { Connectivity = Connectivity.Online });
        if (pathStored)
        {
            Cloud.StoreSavePath(SaveDir);
        }
    }

    public string Write(string rel, string text, DateTimeOffset? mtime = null)
    {
        var full = Path.Combine(SaveDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        File.SetLastWriteTimeUtc(full, (mtime ?? DateTimeOffset.UtcNow).UtcDateTime);
        return full;
    }

    public string? Read(string rel) => File.Exists(Path.Combine(SaveDir, rel)) ? File.ReadAllText(Path.Combine(SaveDir, rel)) : null;

    public IReadOnlyList<string> Files() => SaveFolder.Scan(SaveDir, SaveFilters.None, hash: false).Files.Select(f => f.Path).ToList();

    public async Task<SaveState> SyncAsync(SyncDirection direction = SyncDirection.Auto) =>
        await Service.SyncSaves(new SaveSyncRequest(App, direction));

    public SaveState State() => Saves.Get(App);

    /// <summary>Records "in sync" for the files now in the save folder and the given cloud saves (as if Glider had synced them).</summary>
    public async Task SeedLedgerAsync(params DateTimeOffset[] cloudSaves)
    {
        var local = SaveFolder.Scan(SaveDir, Filters);
        await Saves.Ledger.SetAsync(App, new SaveLedgerEntry(SaveDir, DateTimeOffset.UnixEpoch, local.Files, [.. cloudSaves.Select(CloudManifests.NameFor)], "upload"));
    }

    public async Task<SaveLedgerEntry?> LedgerAsync() => await Saves.Ledger.GetAsync(App);

    public async Task<GameSummary> SummaryAsync() => (await Rig.SummaryAsync(App))!;

    public IReadOnlyList<Notice> Notices() => Rig.Sink.Of<Notice>("notice");

    /// <summary>Plays and closes the game: launch, one poll, exit, grace period.</summary>
    public async Task<LaunchResult> LaunchAsync(bool skipSync = false, Action? whilePlaying = null)
    {
        Glider.Core.Services.ProcessEntry? root = null;
        Rig.Processes.OnStart = (_, p) => root = p;
        var result = await Service.Launch(new LaunchRequest(App, null, skipSync));
        if (result.Outcome != LaunchOutcome.Started)
        {
            return result;
        }

        var sessions = Service.LaunchServiceForTests.Sessions;
        Rig.Time.Advance(TimeSpan.FromSeconds(2));
        sessions.Tick();
        whilePlaying?.Invoke();
        Rig.Processes.Exit(root!.Id);
        sessions.Tick();
        Rig.Time.Advance(TimeSpan.FromSeconds(6));
        sessions.Tick();
        return result;
    }

    public void Dispose() => Rig.Dispose();
}
