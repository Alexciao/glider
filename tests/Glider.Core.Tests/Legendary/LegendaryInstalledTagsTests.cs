using System.Text.Json.Nodes;
using Glider.Core.Legendary;

namespace Glider.Core.Tests.Legendary;

/// <summary>
/// installed.json's install_tags must equal config.ini's, or Legendary's next update/repair deletes every file outside the tags
/// (it pruned 6.7 GB of an imported Fortnite on the owner's machine).
/// </summary>
public sealed class LegendaryInstalledTagsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glider-tags", Guid.NewGuid().ToString("N"));

    public LegendaryInstalledTagsTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "installed.json"), """
            {
              "Fortnite": { "app_name": "Fortnite", "install_path": "D:\\Giochi\\Città\\Fortnite", "install_tags": [], "needs_verification": true, "version": "1" },
              "Other": { "app_name": "Other", "install_tags": ["x", ""] }
            }
            """);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "installed.json")))!;

    [Fact]
    public async Task WritesTheConfigsListAsLegendaryStoresIt_AndKeepsEverythingElse()
    {
        // config.ini "A,B," is what Legendary reads back as ["A", "B", ""] (cli.py: config_tags.split(',')).
        var tags = LegendaryInstalledTags.FromConfigValue("Startup,FortniteBR,");
        Assert.Equal(["Startup", "FortniteBR", ""], tags);

        Assert.True(await LegendaryInstalledTags.SetAsync(_dir, "Fortnite", tags, CancellationToken.None));

        var root = Read();
        Assert.Equal(["Startup", "FortniteBR", ""], root["Fortnite"]!["install_tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.True(root["Fortnite"]!["needs_verification"]!.GetValue<bool>());
        Assert.Equal(@"D:\Giochi\Città\Fortnite", root["Fortnite"]!["install_path"]!.GetValue<string>());
        Assert.Equal(["x", ""], root["Other"]!["install_tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        // ASCII only, like Python's json.dump: Legendary may read it with the locale code page.
        Assert.All(File.ReadAllBytes(Path.Combine(_dir, "installed.json")), b => Assert.True(b < 0x80));
    }

    [Fact]
    public async Task AlreadyEqual_IsLeftUntouched()
    {
        var tags = LegendaryInstalledTags.FromConfigValue("x,");
        var path = Path.Combine(_dir, "installed.json");
        var before = File.ReadAllText(path);

        Assert.True(await LegendaryInstalledTags.SetAsync(_dir, "Other", tags, CancellationToken.None));

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public async Task AGameLegendaryDoesntHave_IsNotAdded() =>
        Assert.False(await LegendaryInstalledTags.SetAsync(_dir, "Nope", ["a"], CancellationToken.None));

    [Fact]
    public async Task WhileLegendaryHoldsItsLock_NothingIsWritten()
    {
        var path = Path.Combine(_dir, "installed.json");
        var before = File.ReadAllText(path);
        using var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        held.Lock(0, 1); // what filelock does in a running Legendary

        await Assert.ThrowsAnyAsync<IOException>(() => LegendaryInstalledTags.SetAsync(_dir, "Fortnite", ["a", ""], CancellationToken.None));
        Assert.Equal(before, File.ReadAllText(path));
    }
}
