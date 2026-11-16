using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glider.Core.Infrastructure;

namespace Glider.Core.Legendary;

/// <summary>
/// Keeps a game's <c>install_tags</c> in Legendary's installed.json equal to the <c>[app] install_tags</c> in config.ini.
/// </summary>
/// <remarks>
/// Legendary compares the two on every install, update and repair; when they differ it treats it as a pack selection change and
/// deletes every file outside the config's tags ("Deleting now untagged files", cli.py:1025-1031 and 1149-1155). An imported
/// game is recorded with no tags, and Glider hands Legendary EGL's tag list through config.ini, so the first update or repair
/// pruned content that was on disk: on the owner's Fortnite, 6.7 GB the game had downloaded on demand. Glider never wants that
/// prune (EGL doesn't do it either), so both files carry the same list. installed.json is written under Legendary's own lock
/// (<c>installed.json.lock</c>, a filelock byte lock; <see cref="FileStream.Lock"/> takes the same one).
/// </remarks>
internal static class LegendaryInstalledTags
{
    // The default encoder escapes non-ASCII (as Python's json.dump does): Legendary may read the file with the locale code page.
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.Default };

    /// <summary>The list Legendary itself stores for a config value: the value split on commas, as is (cli.py: <c>config_tags.split(',')</c>).</summary>
    public static string[] FromConfigValue(string value) => value.Split(',');

    /// <summary>Sets <paramref name="app"/>'s installed.json install_tags to <paramref name="tags"/>.</summary>
    /// <returns>True when written or already equal; false when the game isn't in installed.json.</returns>
    /// <exception cref="IOException">A Legendary process holds installed.json.lock (try again later), or the write failed.</exception>
    public static async Task<bool> SetAsync(string configDir, string app, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var path = LegendaryFiles.InstalledJson(configDir);
        using var lockFile = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var locks = !OperatingSystem.IsMacOS(); // Glider runs on Windows; macOS has no byte-range locks (tests only)
        if (locks)
        {
            lockFile.Lock(0, 1); // IOException while Legendary holds it
        }

        try
        {
            var bytes = await AtomicFile.TryReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (bytes is null || JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) is not JsonObject root ||
                root[app] is not JsonObject game)
            {
                return false;
            }

            if (game["install_tags"] is JsonArray current && current.Select(t => t?.GetValue<string>()).SequenceEqual(tags))
            {
                return true;
            }

            game["install_tags"] = new JsonArray([.. tags.Select(t => (JsonNode?)JsonValue.Create(t))]);
            await AtomicFile.WriteAllTextAsync(path, root.ToJsonString(WriteOptions), keepBackup: true, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (locks && !OperatingSystem.IsMacOS())
            {
                lockFile.Unlock(0, 1);
            }
        }
    }
}
