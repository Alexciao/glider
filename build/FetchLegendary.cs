// Fetches the pinned Legendary release into src/Glider.App/legendary/ and verifies SHA-256.
// Usage: dotnet run build/FetchLegendary.cs [-- <repo-root>]
// Pins live in build/legendary.json. Idempotent: files whose hash already matches are left alone.
using System.Security.Cryptography;
using System.Text.Json;

try
{
    return await RunAsync(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FetchLegendary: FAILED: {ex.Message}");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    var root = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRoot();
    var pinPath = Path.Combine(root, "build", "legendary.json");
    using var pinDoc = JsonDocument.Parse(await File.ReadAllTextAsync(pinPath));
    string Pin(string name) => pinDoc.RootElement.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"{pinPath}: '{name}' is null");

    var destDir = Path.Combine(root, "src", "Glider.App", "legendary");
    Directory.CreateDirectory(destDir);

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("glider-build");

    await EnsureAsync(http, "legendary.exe", Pin("url"), Pin("sha256"), Path.Combine(destDir, "legendary.exe"));
    await EnsureAsync(http, "LICENSE.txt", Pin("licenseUrl"), Pin("licenseSha256"), Path.Combine(destDir, "LICENSE.txt"));
    Console.WriteLine($"Legendary {Pin("version")} ready in {destDir}");
    return 0;
}

static async Task EnsureAsync(HttpClient http, string label, string url, string expected, string dest)
{
    if (File.Exists(dest) && string.Equals(await HashAsync(dest), expected, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"{label}: up to date (sha256 matches), skipping download");
        return;
    }

    Console.WriteLine($"{label}: downloading {url}");
    var tmp = Path.Combine(Path.GetTempPath(), $"glider-fetch-{Guid.NewGuid():N}.tmp");
    try
    {
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst);
        }

        var actual = await HashAsync(tmp);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label}: SHA-256 mismatch.\n  expected {expected}\n  actual   {actual}\n  url      {url}");

        File.Move(tmp, dest, overwrite: true);
        Console.WriteLine($"{label}: verified and installed ({new FileInfo(dest).Length:N0} bytes)");
    }
    finally
    {
        if (File.Exists(tmp)) File.Delete(tmp);
    }
}

static async Task<string> HashAsync(string path)
{
    await using var fs = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs));
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Glider.slnx"))) return dir.FullName;
    throw new DirectoryNotFoundException("Glider.slnx not found above the current directory; pass the repo root as an argument.");
}
