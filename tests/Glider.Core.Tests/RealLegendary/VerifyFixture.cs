using System.Text.Json.Nodes;

namespace Glider.Core.Tests.RealLegendary;

/// <summary>
/// A tiny but complete install the real exe can verify: <c>GliderVerify</c>, three files, and the manifest Legendary 0.21.1's own
/// serializer wrote for them (Fixtures/real-legendary/make_verify_manifest.py). The files are re-created here from the same byte pattern,
/// so the fixture is a byte rule plus a 337-byte manifest, with no committed game data.
/// </summary>
internal static class VerifyFixture
{
    public const string App = "GliderVerify";
    public const string Version = "1.0.0-verify";
    public const string ManifestName = "GliderVerify_Windows_1.0.0-verify.manifest";

    /// <summary>Keep in sync with FILES in make_verify_manifest.py.</summary>
    public static readonly (string Path, int Size, int Seed)[] Files =
    [
        ("Game.exe", 4000, 7),
        ("Data/assets.bin", 70000, 11),
        ("Data/notes.txt", 150, 13),
    ];

    public static string ManifestSource => Path.Combine(AppContext.BaseDirectory, "Fixtures", "real-legendary", ManifestName);

    public static byte[] Pattern(int size, int seed)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)((i * 31 + seed) & 0xFF);
        }

        return bytes;
    }

    public static string FilePath(string folder, string relative) => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Writes the three files under <paramref name="folder"/>.</summary>
    public static void WriteFiles(string folder)
    {
        foreach (var (path, size, seed) in Files)
        {
            var full = FilePath(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Pattern(size, seed));
        }
    }

    /// <summary>
    /// Installs the fixture into <paramref name="env"/>: files in <c>Games\GliderVerify</c>, the manifest in <c>manifests\</c>, installed.json,
    /// metadata/<c>assets.json</c>/<c>entitlements.json</c> (library files in the real on-disk format). Returns the install folder.
    /// </summary>
    public static string Install(RealLegendaryEnv env, string? folder = null)
    {
        folder ??= Path.Combine(env.Games, App);
        WriteFiles(folder);
        Directory.CreateDirectory(Path.Combine(env.Config, "manifests"));
        File.Copy(ManifestSource, Path.Combine(env.Config, "manifests", ManifestName), overwrite: true);
        env.Library
            .Add(App, LibraryFixture.Item("Glider Verify", "fakens", "00000000000000000000000000000001", attributes: new()
            {
                ["CanRunOffline"] = "true",
                ["FolderName"] = App,
                ["AdditionalCommandLine"] = "-meta_arg",
                ["extraLaunchOption_000_Name"] = "Safe mode",
                ["extraLaunchOption_000_Args"] = "-safe -nosound",
            }), Version)
            .Install(App, folder, Version, executable: "Game.exe", title: "Glider Verify")
            .Save();
        return folder;
    }

    /// <summary>installed.json as parsed JSON (the exe rewrites it; tests read what it wrote).</summary>
    public static JsonObject ReadInstalled(RealLegendaryEnv env) => JsonNode.Parse(File.ReadAllText(env.InstalledJson))!.AsObject();
}
