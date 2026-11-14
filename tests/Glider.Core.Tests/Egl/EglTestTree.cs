using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glider.Core.Contracts;
using Glider.Core.Egl;

namespace Glider.Core.Tests.Egl;

/// <summary>
/// A throw-away machine under a temp dir: fake ProgramData (EGL manifests + LauncherInstalled.dat), fake drives with
/// Fortnite folders, and Glider's AppPaths. Never touches the real C:\ProgramData\Epic.
/// </summary>
internal sealed class EglTestTree : IDisposable
{
    public const string V3310 = "++Fortnite+Release-33.10-CL-45000000-Windows";
    public const string V3320 = "++Fortnite+Release-33.20-CL-46000000-Windows";
    public const string V3300 = "++Fortnite+Release-33.00-CL-44000000-Windows";

    public static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Egl");

    public static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(FixturesDir, name));

    public EglTestTree(bool eglInstalled = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "glider-egl-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
        Environment = new EglEnvironment(Path.Combine(Root, "ProgramData"));
        Paths = new AppPaths(Path.Combine(Root, "LocalAppData", "Glider"), Path.Combine(Root, "legendary.exe"));
        Paths.EnsureCreated();
        if (eglInstalled)
        {
            Directory.CreateDirectory(Environment.ManifestsDir);
        }
    }

    public string Root { get; }
    public EglEnvironment Environment { get; }
    public AppPaths Paths { get; }
    public FakeProcesses Processes { get; } = new();
    public FakeSettings Settings { get; } = new();
    public TestLog Log { get; } = new();

    public EglService CreateService(EglServiceOptions? options = null) =>
        new(Environment, Paths, Log, Processes, options ?? FastOptions());

    public static EglServiceOptions FastOptions(Action<string>? beforeCommit = null) => new()
    {
        BusyPollInterval = TimeSpan.FromMilliseconds(50),
        WriteRetryDelay = TimeSpan.FromMilliseconds(5),
        BeforeCommitHook = beforeCommit,
    };

    /// <summary>A path on a fake drive, e.g. Drive("E", "Games", "Fortnite").</summary>
    public string Drive(string letter, params string[] parts) => Path.Combine([Root, "drive-" + letter, .. parts]);

    /// <summary>Creates a fake game folder for any app: a file plus .egstore with manifest/mancpn declaring <paramref name="app"/>.</summary>
    public string CreateOtherGame(string path, string app, string guid)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "Game.exe"), "MZ fake"u8.ToArray());
        var egstore = Path.Combine(path, ".egstore");
        Directory.CreateDirectory(egstore);
        File.WriteAllBytes(Path.Combine(egstore, guid + ".manifest"), Encoding.ASCII.GetBytes("EGL-MANIFEST-" + guid));
        File.WriteAllText(Path.Combine(egstore, guid + ".mancpn"),
            "{\r\n\t\"FormatVersion\": 0,\r\n\t\"AppName\": \"" + app + "\",\r\n\t\"CatalogNamespace\": \"ns\",\r\n\t\"CatalogItemId\": \"item\"\r\n}");
        return path;
    }

    /// <summary>Writes an EGL .item for any app at a location.</summary>
    public string WriteOtherItem(string installLocation, string app, string version, string guid, string? displayName = null, string? mainGame = null) =>
        WriteItem(installLocation, version, guid, n =>
        {
            n["AppName"] = app;
            n["MainGameAppName"] = mainGame ?? app;
            n["DisplayName"] = displayName ?? app;
            n["MandatoryAppFolderName"] = EglPath.LeafName(installLocation);
        });

    /// <summary>Creates a fake Fortnite folder: just the shipping exe (and optionally .egstore with manifest/mancpn).</summary>
    public string CreateGame(string path, string? egstoreGuid = null, bool withExe = true)
    {
        Directory.CreateDirectory(path);
        if (withExe)
        {
            var exe = FortniteLayout.ShippingExe(path);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, "MZ fake"u8.ToArray());
        }

        if (egstoreGuid is not null)
        {
            var egstore = Path.Combine(path, ".egstore");
            Directory.CreateDirectory(egstore);
            File.WriteAllBytes(Path.Combine(egstore, egstoreGuid + ".manifest"), Encoding.ASCII.GetBytes("EGL-MANIFEST-" + egstoreGuid));
            File.WriteAllText(Path.Combine(egstore, egstoreGuid + ".mancpn"),
                "{\r\n\t\"FormatVersion\": 0,\r\n\t\"AppName\": \"Fortnite\",\r\n\t\"CatalogNamespace\": \"fn\",\r\n\t\"CatalogItemId\": \"4fe75bbc5a674f4f9b356b5c90567da5\"\r\n}");
        }

        return path;
    }

    /// <summary>
    /// Writes an EGL-style .item (tabs, CRLF, EGL key order) based on the realistic fixture, overriding the given keys.
    /// Returns the file path.
    /// </summary>
    public string WriteItem(string installLocation, string version = V3310, string? guid = null,
        Action<JsonObject>? edit = null, string? fileName = null, string? dir = null)
    {
        guid ??= EglService.NewGuid();
        var node = JsonNode.Parse(Fixture("fortnite-egl.item"))!.AsObject();
        node["InstallLocation"] = installLocation;
        node["ManifestLocation"] = installLocation + "/.egstore";
        node["StagingLocation"] = installLocation + "/.egstore/bps";
        node["InstallationGuid"] = guid;
        node["AppVersionString"] = version;
        edit?.Invoke(node);
        var path = Path.Combine(dir ?? Environment.ManifestsDir, fileName ?? guid + ".item");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EglStyle(node));
        return path;
    }

    public static byte[] EglStyle(JsonNode node)
    {
        var json = node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = '\t',
            IndentSize = 1,
            NewLine = "\r\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>Writes LauncherInstalled.dat with the given (location, version, appName) entries, EGL style.</summary>
    public string WriteLauncherInstalled(params (string Location, string Version, string AppName)[] entries)
    {
        var list = new JsonArray();
        foreach (var (location, version, app) in entries)
        {
            list.Add(new JsonObject
            {
                ["InstallLocation"] = location,
                ["NamespaceId"] = app == "Fortnite" ? "fn" : "ue",
                ["ItemId"] = app == "Fortnite" ? "4fe75bbc5a674f4f9b356b5c90567da5" : "f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6",
                ["ArtifactId"] = app,
                ["AppVersion"] = version,
                ["AppName"] = app,
            });
        }

        var path = Environment.LauncherInstalledDat;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EglStyle(new JsonObject { ["InstallationList"] = list }));
        return path;
    }

    public string LegendaryManifest(string version, string content = "LEGENDARY-MANIFEST")
    {
        var path = Path.Combine(Paths.LegendaryConfig, "manifests", $"Fortnite_Windows_{version.Replace('+', '_')}.manifest");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content + ":" + version);
        return path;
    }

    public static void Age(string path, TimeSpan age)
    {
        var t = DateTime.UtcNow - age;
        if (Directory.Exists(path))
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            {
                if (File.Exists(e))
                {
                    File.SetLastWriteTimeUtc(e, t);
                }
                else
                {
                    Directory.SetLastWriteTimeUtc(e, t);
                }
            }

            Directory.SetLastWriteTimeUtc(path, t);
        }
        else
        {
            File.SetLastWriteTimeUtc(path, t);
        }
    }

    public string[] TempLeftovers() =>
        Directory.EnumerateFiles(Root, "*.glider.tmp", SearchOption.AllDirectories).ToArray();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }
}

/// <summary>SHA-256 (+ size and mtime) of every file under the EGL dirs and the given installs' .egstore folders.</summary>
internal static class EglTreeHasher
{
    public static IReadOnlyDictionary<string, string> Hash(EglEnvironment env, params string[] installPaths)
    {
        var roots = new List<string> { Path.Combine(env.ProgramData, "Epic") };
        roots.AddRange(installPaths.Select(p => Path.Combine(p, ".egstore")));
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(f);
                var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)));
                map[f] = $"{hash} {info.Length} {info.LastWriteTimeUtc.Ticks}";
            }

            foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                map[d + Path.DirectorySeparatorChar] = "dir";
            }
        }

        return map;
    }

    public static void AssertSame(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (k, v) in before)
        {
            Assert.True(v == after[k], $"{k} changed: {v} -> {after[k]}");
        }
    }
}

internal sealed class FakeProcesses : IProcessProbe
{
    public volatile bool EglRunning;
    public bool IsRunning(string processName) => processName == "EpicGamesLauncher" && EglRunning;
}

internal sealed class FakeSettings : ISettingsStore
{
    public Settings Current { get; set; } = Settings.Default;
    public event Action<Settings>? Changed;
    public Task<Settings> UpdateAsync(Settings settings)
    {
        Current = settings;
        Changed?.Invoke(settings);
        return Task.FromResult(settings);
    }
}

internal sealed class TestLog : ILog
{
    private readonly List<string> _lines = [];
    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;
    public string CurrentFile => "";
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToList();
            }
        }
    }

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        lock (_lines)
        {
            _lines.Add($"{level} [{category}] {message}{(exception is null ? "" : " :: " + exception.Message)}");
        }
    }

    public string Tail(int maxLines) => string.Join('\n', Lines.TakeLast(maxLines));
}

internal static class Make
{
    public static InstallRecord For(string path, string version, string? guid = null, string app = FortniteLayout.AppName) =>
        new(app, path, version, 100, DateTimeOffset.UnixEpoch, AdoptedFromEgl: true, guid, NeedsVerification: false);

    public static EglWriteBack WriteBack(string path, string version, string? manifest, string? guid = null, IReadOnlyList<string>? tags = null) =>
        new(FortniteLayout.AppName, path, version, 101234567890, tags ?? [], manifest,
            "4fe75bbc5a674f4f9b356b5c90567da5", "fn", "FortniteGame/Binaries/Win64/FortniteLauncher.exe", guid, "Fortnite");
}
