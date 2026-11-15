// Fetches the pinned Legendary release into src/Glider.App/legendary/ and verifies SHA-256.
// A pin whose url ends in .zip is a PyInstaller --onedir build (legendary.exe + _internal/ at the zip root, see the legendary-fork workflow):
// the zip is verified against "sha256", extracted, and the extracted legendary.exe is verified against "exeSha256" (what Core checks at run time).
// A pin whose url is a lone .exe (the stock one-file build) is still supported.
// Usage: dotnet run build/FetchLegendary.cs [-- [--fork] [<repo-root>]]
// Pins live in build/legendary.json (--fork: build/legendary-fork.json, the Glider fork, which also ships its patch and source offer).
// Idempotent: files whose hash already matches are left alone.
// Private repo: with GH_TOKEN or GITHUB_TOKEN set, a github.com release-asset URL is downloaded through the GitHub REST API
// (anonymous release downloads 404 on a private repo). The token is only ever sent as an Authorization header, never printed.
// The fork is used when --fork is given or the pinned version says "-glider"; it also fetches SOURCE-OFFER.txt and glider-legendary.patch.
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
    var forkFlag = args.Contains("--fork");
    var positional = args.Where(a => a != "--fork").ToArray();
    var root = positional.Length > 0 ? Path.GetFullPath(positional[0]) : FindRoot();
    var pinPath = Path.Combine(root, "build", forkFlag ? "legendary-fork.json" : "legendary.json");
    using var pinDoc = JsonDocument.Parse(await File.ReadAllTextAsync(pinPath));
    var fork = forkFlag || (pinDoc.RootElement.GetProperty("version").GetString() ?? "").Contains("-glider", StringComparison.OrdinalIgnoreCase);
    string Pin(string name) => pinDoc.RootElement.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"{pinPath}: '{name}' is null");

    var destDir = Path.Combine(root, "src", "Glider.App", "legendary");
    Directory.CreateDirectory(destDir);

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("glider-build");

    var token = Environment.GetEnvironmentVariable("GH_TOKEN") is { Length: > 0 } t1 ? t1
        : Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } t2 ? t2 : null;
    var fetcher = new Downloader(http, token);

    var exeUrl = Pin("url");
    if (exeUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        await EnsureArchiveAsync(fetcher, exeUrl, Pin("sha256"), Pin("exeSha256"), destDir);
    else
        await EnsureAsync(fetcher, "legendary.exe", exeUrl, Pin("sha256"), Path.Combine(destDir, "legendary.exe"));
    await EnsureAsync(fetcher, "LICENSE.txt", Pin("licenseUrl"), Pin("licenseSha256"), Path.Combine(destDir, "LICENSE.txt"));
    if (fork)
    {
        // GPLv3: the patch and the source offer travel with the modified binary; both are assets of the same release as the exe.
        foreach (var name in new[] { "glider-legendary.patch", "SOURCE-OFFER.txt" })
        {
            var url = exeUrl[..(exeUrl.LastIndexOf('/') + 1)] + name;
            Console.WriteLine($"{name}: downloading {url}");
            var tmp = Path.Combine(Path.GetTempPath(), $"glider-fetch-{Guid.NewGuid():N}.tmp");
            try
            {
                await fetcher.DownloadAsync(url, tmp);
                if (new FileInfo(tmp).Length == 0) throw new InvalidDataException($"{name}: empty download from {url}");
                File.Move(tmp, Path.Combine(destDir, name), overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }

    Console.WriteLine($"Legendary {Pin("version")} ready in {destDir}");
    return 0;
}

static async Task EnsureAsync(Downloader fetcher, string label, string url, string expected, string dest)
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
        await fetcher.DownloadAsync(url, tmp);

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

// Downloads the onedir zip, verifies it, extracts into a staging folder next to the destination, verifies the extracted exe, then swaps
// the folder in (legendary.exe is removed first and moved in last, so an interrupted run never leaves a matching exe without its _internal).
static async Task EnsureArchiveAsync(Downloader fetcher, string url, string zipSha256, string exeSha256, string destDir)
{
    var exe = Path.Combine(destDir, "legendary.exe");
    var internalDir = Path.Combine(destDir, "_internal");
    if (File.Exists(exe) && Directory.Exists(internalDir) && string.Equals(await HashAsync(exe), exeSha256, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("legendary.exe + _internal: up to date (exe sha256 matches), skipping download");
        return;
    }

    Console.WriteLine($"legendary (onedir zip): downloading {url}");
    var tmp = Path.Combine(Path.GetTempPath(), $"glider-fetch-{Guid.NewGuid():N}.zip");
    var staging = Path.Combine(destDir, $".staging-{Guid.NewGuid():N}");
    try
    {
        await fetcher.DownloadAsync(url, tmp);
        var actual = await HashAsync(tmp);
        if (!string.Equals(actual, zipSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"legendary zip: SHA-256 mismatch.\n  expected {zipSha256}\n  actual   {actual}\n  url      {url}");

        System.IO.Compression.ZipFile.ExtractToDirectory(tmp, staging); // refuses entries that would land outside the folder
        var stagedExe = Path.Combine(staging, "legendary.exe");
        if (!File.Exists(stagedExe) || !Directory.Exists(Path.Combine(staging, "_internal")))
            throw new InvalidDataException("legendary zip: expected legendary.exe and _internal/ at the archive root");
        var exeActual = await HashAsync(stagedExe);
        if (!string.Equals(exeActual, exeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"legendary.exe in the zip: SHA-256 mismatch.\n  expected {exeSha256}\n  actual   {exeActual}");

        if (File.Exists(exe)) File.Delete(exe);
        if (Directory.Exists(internalDir)) Directory.Delete(internalDir, recursive: true);
        foreach (var entry in Directory.EnumerateFileSystemEntries(staging).Where(e => !string.Equals(Path.GetFileName(e), "legendary.exe", StringComparison.OrdinalIgnoreCase)))
        {
            var target = Path.Combine(destDir, Path.GetFileName(entry));
            if (Directory.Exists(entry)) { if (Directory.Exists(target)) Directory.Delete(target, true); Directory.Move(entry, target); }
            else File.Move(entry, target, overwrite: true);
        }
        File.Move(stagedExe, exe);
        var files = Directory.EnumerateFiles(internalDir, "*", SearchOption.AllDirectories).Count();
        Console.WriteLine($"legendary: verified and installed (legendary.exe {new FileInfo(exe).Length:N0} bytes + _internal, {files} files)");
    }
    finally
    {
        if (File.Exists(tmp)) File.Delete(tmp);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
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

sealed class Downloader(HttpClient http, string? token)
{
    // https://github.com/{owner}/{repo}/releases/download/{tag}/{asset}
    private static readonly System.Text.RegularExpressions.Regex ReleaseAsset = new(
        @"^https://github\.com/(?<o>[^/]+)/(?<r>[^/]+)/releases/download/(?<t>[^/]+)/(?<a>[^/?#]+)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public async Task DownloadAsync(string url, string dest)
    {
        var m = ReleaseAsset.Match(url);
        if (token is null || !m.Success)
        {
            await SaveAsync(new HttpRequestMessage(HttpMethod.Get, url), dest);
            return;
        }

        string Esc(string g) => Uri.EscapeDataString(Uri.UnescapeDataString(m.Groups[g].Value));
        var api = $"https://api.github.com/repos/{Esc("o")}/{Esc("r")}";
        var wanted = Uri.UnescapeDataString(m.Groups["a"].Value);

        using var rel = Api(HttpMethod.Get, $"{api}/releases/tags/{Esc("t")}", "application/vnd.github+json");
        using var relResp = await http.SendAsync(rel);
        if (!relResp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub API: release '{Uri.UnescapeDataString(m.Groups["t"].Value)}' of {m.Groups["o"].Value}/{m.Groups["r"].Value} returned {(int)relResp.StatusCode}");
        using var doc = JsonDocument.Parse(await relResp.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == wanted)
            .Select(a => (long?)a.GetProperty("id").GetInt64()).FirstOrDefault()
            ?? throw new FileNotFoundException($"release has no asset named '{wanted}'");

        await SaveAsync(Api(HttpMethod.Get, $"{api}/releases/assets/{id}", "application/octet-stream"), dest);
    }

    private HttpRequestMessage Api(HttpMethod method, string url, string accept)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.ParseAdd(accept);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private async Task SaveAsync(HttpRequestMessage req, string dest)
    {
        using (req)
        {
            // The API answers 302 to a pre-signed storage URL; HttpClient follows it and drops the Authorization header
            // when the host changes, which is what we want (storage rejects a Bearer token).
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"download returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {req.RequestUri?.GetLeftPart(UriPartial.Path)}");
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst);
        }
    }
}
