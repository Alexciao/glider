using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FakeLegendary;

/// <summary>
/// A fake legendary.exe for tests. It resolves the sub-command like Legendary does (global flags first), logs the
/// invocation to <c>$LEGENDARY_CONFIG_PATH/fake/invocations.log</c>, then executes the script
/// <c>fake/&lt;command&gt;.&lt;n&gt;.script</c> (n = 1-based invocation count of that command) or <c>fake/&lt;command&gt;.script</c>.
///
/// Script directives (one per line, '#' comments; text supports \t \r \n \\ \uXXXX escapes and {basepath} {config} {arg:N}):
///   err TEXT / out TEXT          line to stderr (ANSI code page + Python backslashreplace) / stdout (UTF-8), CRLF-terminated
///   errraw TEXT / outraw TEXT    same without a line terminator
///   errfile PATH [ms] / outfile PATH [ms]   replay a fixture line by line (leading '#' header lines skipped)
///   errbin PATH                  raw bytes to stderr
///   sleep MS | hang | exit CODE | child | readstdin
///   download chunks=N files=F mib=M interval=MS [app=A] [stallat=PCT] [spam] [writefail=K [diskfull|locked]]
///   verify files=N interval=MS [app=A] [corrupt=K] [missing=K] [locked=K]
///   installed version=V path=P [app=A] [tags=a,b] [needsverify]   |   uninstalled [app=A]
///   writefile RELPATH TEXT | deletefile RELPATH
/// </summary>
internal static class Program
{
    private static readonly Stream StdOut = Console.OpenStandardOutput();
    private static readonly Stream StdErr = Console.OpenStandardError();
    private static Encoding _ansi = Encoding.Latin1;
    private static string _config = "";
    private static string _command = "";
    private static List<string> _positional = [];
    private static Dictionary<string, string> _options = [];

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fake-child")
        {
            File.WriteAllText(args[1], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _ansi = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        _config = Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH") ?? "";
        if (_config.Length == 0)
        {
            Err("[fake] CRITICAL: LEGENDARY_CONFIG_PATH not set");
            return 3;
        }

        foreach (var d in new[] { "", "manifests", "metadata", "tmp", "fake" })
        {
            Directory.CreateDirectory(Path.Combine(_config, d));
        }

        ParseArgs(args);
        var fakeDir = Path.Combine(_config, "fake");
        var counter = Path.Combine(fakeDir, _command + ".count");
        var n = (File.Exists(counter) ? int.Parse(File.ReadAllText(counter), CultureInfo.InvariantCulture) : 0) + 1;
        File.WriteAllText(counter, n.ToString(CultureInfo.InvariantCulture));
        File.AppendAllText(Path.Combine(fakeDir, "invocations.log"), _command + "\t" + string.Join('\u001f', args) + "\n");

        var script = Path.Combine(fakeDir, $"{_command}.{n}.script");
        if (!File.Exists(script))
        {
            script = Path.Combine(fakeDir, $"{_command}.script");
        }

        if (!File.Exists(script))
        {
            Err($"[fake] CRITICAL: no script for \"{_command}\"");
            return 3;
        }

        foreach (var raw in File.ReadAllLines(script))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var sp = line.IndexOf(' ');
            var verb = sp < 0 ? line : line[..sp];
            var rest = sp < 0 ? "" : line[(sp + 1)..];
            switch (verb)
            {
                case "err": Err(Expand(rest)); break;
                case "out": Out(Expand(rest)); break;
                case "errraw": Write(StdErr, Expand(rest), ansi: true); break;
                case "outraw": Write(StdOut, Expand(rest), ansi: false); break;
                case "errfile": Replay(rest, toErr: true); break;
                case "outfile": Replay(rest, toErr: false); break;
                case "errbin": StdErr.Write(File.ReadAllBytes(ResolvePath(rest))); StdErr.Flush(); break;
                case "sleep": Thread.Sleep(int.Parse(rest, CultureInfo.InvariantCulture)); break;
                case "hang": Thread.Sleep(Timeout.Infinite); break;
                case "exit": return int.Parse(rest, CultureInfo.InvariantCulture);
                case "child": SpawnChild(); break;
                case "readstdin": if (ReadStdin() is { } code) { return code; } break;
                case "download": Download(KeyValues(rest)); break;
                case "verify": Verify(KeyValues(rest)); break;
                case "installed": Installed(KeyValues(rest)); break;
                case "uninstalled": Uninstalled(KeyValues(rest)); break;
                case "writefile":
                {
                    var s2 = rest.IndexOf(' ');
                    var target = Path.Combine(_config, Expand(rest[..s2]));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, Expand(rest[(s2 + 1)..]));
                    break;
                }

                case "deletefile": File.Delete(Path.Combine(_config, Expand(rest))); break;
                default: Err($"[fake] CRITICAL: unknown directive {verb}"); return 4;
            }
        }

        return 0;
    }

    private static void ParseArgs(string[] args)
    {
        var i = 0;
        while (i < args.Length && args[i].StartsWith('-'))
        {
            if (args[i] is "-V" or "--version")
            {
                _command = "version";
                return;
            }

            i += args[i] is "-A" or "--api-timeout" ? 2 : 1;
        }

        _command = i < args.Length ? args[i] : "help";
        for (i++; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) &&
                               args[i] is "--base-path" or "--max-workers" or "--max-shared-memory" or "--preferred-cdn" or "--code" or "--path";
                _options[args[i]] = hasValue ? args[++i] : "true";
            }
            else if (args[i] != "-y")
            {
                _positional.Add(args[i]);
            }
        }

        // `eos-overlay <install|update|remove|...>`: one script per action (fake/eos-overlay-install.script).
        if (_command == "eos-overlay" && _positional.Count > 0)
        {
            _command = "eos-overlay-" + _positional[0];
        }
    }

    // ------------------------------------------------------------------ output

    private static void Out(string text) => Write(StdOut, text + "\r\n", ansi: false);

    private static void Err(string text) => Write(StdErr, text + "\r\n", ansi: true);

    private static void Write(Stream s, string text, bool ansi)
    {
        var bytes = ansi ? EncodeBackslashReplace(text) : Encoding.UTF8.GetBytes(text);
        s.Write(bytes);
        s.Flush();
    }

    /// <summary>Python's errors='backslashreplace' over cp1252, as Legendary's stderr really is.</summary>
    private static byte[] EncodeBackslashReplace(string text)
    {
        var ms = new MemoryStream();
        foreach (var rune in text.EnumerateRunes())
        {
            var s = rune.ToString();
            try
            {
                ms.Write(_ansi.GetBytes(s));
            }
            catch (EncoderFallbackException)
            {
                var esc = rune.Value <= 0xFF ? $"\\x{rune.Value:x2}" : rune.Value <= 0xFFFF ? $"\\u{rune.Value:x4}" : $"\\U{rune.Value:x8}";
                ms.Write(Encoding.ASCII.GetBytes(esc));
            }
        }

        return ms.ToArray();
    }

    /// <summary>Replays a fixture byte-for-byte (fixtures are already in Legendary's stream encodings), CRLF per line.</summary>
    private static void Replay(string rest, bool toErr)
    {
        var parts = rest.Split(' ', 2);
        var delay = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var bytes = File.ReadAllBytes(ResolvePath(parts[0]));
        var stream = toErr ? StdErr : StdOut;
        var header = true;
        var start = 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            var next = end < 0 ? bytes.Length : end + 1;
            var len = (end < 0 ? bytes.Length : end) - start;
            if (len > 0 && bytes[start + len - 1] == (byte)'\r')
            {
                len--;
            }

            if (!(header && len > 0 && bytes[start] == (byte)'#'))
            {
                header = false;
                stream.Write(bytes, start, len);
                stream.Write("\r\n"u8);
                stream.Flush();
                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }
            }

            start = next;
        }
    }

    private static string ResolvePath(string p)
    {
        p = Expand(p);
        return Path.IsPathRooted(p) ? p : Path.Combine(_config, "fake", p);
    }

    private static string Expand(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                switch (next)
                {
                    case 't': sb.Append('\t'); i++; continue;
                    case 'r': sb.Append('\r'); i++; continue;
                    case 'n': sb.Append('\n'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                    case 'u' when i + 6 <= text.Length &&
                                  int.TryParse(text.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp):
                        sb.Append((char)cp);
                        i += 5;
                        continue;
                }
            }

            sb.Append(c);
        }

        var s = sb.ToString().Replace("{config}", _config, StringComparison.Ordinal)
            .Replace("{basepath}", _options.GetValueOrDefault("--base-path", ""), StringComparison.Ordinal);
        for (var a = 0; a < _positional.Count; a++)
        {
            s = s.Replace($"{{arg:{a}}}", _positional[a], StringComparison.Ordinal);
        }

        return s;
    }

    private static Dictionary<string, string> KeyValues(string rest)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in Tokenize(rest))
        {
            var eq = token.IndexOf('=');
            d[eq < 0 ? token : token[..eq]] = eq < 0 ? "true" : Expand(token[(eq + 1)..]);
        }

        return d;
    }

    private static IEnumerable<string> Tokenize(string s)
    {
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in s)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (c == ' ' && !quoted)
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }

                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ behaviours

    private static void SpawnChild()
    {
        var pidFile = Path.Combine(_config, "fake", "child.pid");
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--fake-child");
        psi.ArgumentList.Add(pidFile);
        Process.Start(psi);
        for (var i = 0; i < 200 && !File.Exists(pidFile); i++)
        {
            Thread.Sleep(10);
        }
    }

    private static int? ReadStdin()
    {
        if (Console.In.Read() >= 0)
        {
            Out("stdin had data");
            return null;
        }

        Err("Traceback (most recent call last):");
        Err("  File \"cli.py\", line 3262, in <module>");
        Err("EOFError: EOF when reading a line");
        Err($"[PYI-{Environment.ProcessId}:ERROR] Failed to execute script 'cli' due to unhandled exception!");
        return 1;
    }

    private static string App(Dictionary<string, string> kv) => kv.GetValueOrDefault("app", "Fortnite");

    /// <summary>Renders DLManager output with the verbatim format strings of downloader/mp/manager.py:766-775.</summary>
    private static void Download(Dictionary<string, string> kv)
    {
        var app = App(kv);
        var chunks = long.Parse(kv["chunks"], CultureInfo.InvariantCulture);
        var files = int.Parse(kv["files"], CultureInfo.InvariantCulture);
        var mib = double.Parse(kv["mib"], CultureInfo.InvariantCulture);
        var interval = int.Parse(kv.GetValueOrDefault("interval", "20"), CultureInfo.InvariantCulture);
        var stallAt = kv.TryGetValue("stallat", out var sa) ? double.Parse(sa, CultureInfo.InvariantCulture) : (double?)null;
        var writeFailAt = kv.TryGetValue("writefail", out var wf) ? int.Parse(wf, CultureInfo.InvariantCulture) : -1;
        var resume = Path.Combine(_config, "tmp", app + ".resume");
        var done = File.Exists(resume) ? File.ReadAllLines(resume).Count(l => l.Length > 0) : 0;
        done = Math.Min(done, files);
        if (done > 0)
        {
            Err("[DLM] INFO: Found previously interrupted download. Download will be resumed if possible.");
            Err($"[DLM] INFO: Skipping {done} files based on resume data.");
        }

        var remainingFiles = files - done;
        var tasks = Math.Max(1, chunks * remainingFiles / files);
        var dlMiB = mib * remainingFiles / files;
        var installMiB = mib * 1.6;
        Err($"[cli] INFO: Install size: {F2(installMiB)} MiB");
        Err($"[cli] INFO: Download size: {F2(dlMiB)} MiB (Compression savings: 37.5%)");
        Err($"[cli] INFO: Reusable size: 0.00 MiB (chunks) / {F2(installMiB * done / files)} MiB (unchanged / skipped)");
        Err("[cli] INFO: Downloads are resumable, you can interrupt the download with CTRL-C and resume it using the same command later on.");
        Err($"[DLManager] INFO: Download Manager running with process-id: {Environment.ProcessId}");
        Err("[DLManager] INFO: Starting download workers...");
        Err("[DLManager] INFO: Starting file writing worker...");

        long processed = 0;
        double totalDl = 0;
        var perFileMiB = remainingFiles > 0 ? dlMiB / remainingFiles : 0;
        for (var i = 0; i < remainingFiles; i++)
        {
            Thread.Sleep(interval);
            var fileIndex = done + i;
            if (stallAt is { } pct && fileIndex * 100.0 / files >= pct)
            {
                Stall(kv.ContainsKey("spam"), processed, tasks, totalDl, i, interval);
            }

            processed = tasks * (i + 1) / remainingFiles;
            totalDl += perFileMiB;
            Block(processed, tasks, i + 1, totalDl, totalDl * 1.6, perFileMiB * 1000 / interval);
            if (fileIndex == writeFailAt)
            {
                Err(kv.ContainsKey("diskfull")
                    ? "[FileWorker] WARNING: Something in writing a file failed: OSError(28, 'No space left on device')"
                    : kv.ContainsKey("locked")
                        ? "[FileWorker] WARNING: Something in writing a file failed: PermissionError(13, 'The process cannot access the file because it is being used by another process')"
                        : "[FileWorker] WARNING: Something in writing a file failed: OSError(22, 'Invalid argument')");
                Err($"[DLManager] CRITICAL: Writing for FortniteGame/Content/Paks/pakchunk{fileIndex}-WindowsClient.pak failed!");
            }
            else
            {
                var name = $"FortniteGame/Content/Paks/pakchunk{fileIndex}-WindowsClient.pak";
                File.AppendAllText(resume, $"{Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(name)))}:{name}\n");
            }
        }

        Err("[DLManager] INFO: Waiting for installation to finish...");
        File.Delete(resume);
        Err("[DLManager] INFO: All done! Download manager quitting...");
    }

    private static void Stall(bool spam, long processed, long tasks, double totalDl, int ticks, int interval)
    {
        if (!spam)
        {
            Thread.Sleep(Timeout.Infinite);
        }

        for (var n = 0; ; n++)
        {
            Thread.Sleep(interval);
            Err($"[DLWorker {n % 4 + 1}] WARNING: Chunk download for 7{n:D5} failed: (ConnectionError(MaxRetryError(\"HTTPSConnectionPool(host='epicgames-download1.akamaized.net', port=443): Max retries exceeded\")), retrying...");
            Block(processed, tasks, ticks + n, totalDl, totalDl * 1.6, 0);
        }
    }

    private static void Block(long processed, long tasks, int seconds, double dlMiB, double writtenMiB, double speedMiB)
    {
        var perc = processed * 100.0 / tasks;
        var eta = processed > 0 ? (tasks - processed) / (processed / (double)Math.Max(1, seconds)) : 0;
        Err($"[DLManager] INFO: = Progress: {F2(perc)}% ({processed}/{tasks}), Running for {Clock(seconds)}, ETA: {Clock(processed > 0 ? eta : 0)}");
        Err($"[DLManager] INFO:  - Downloaded: {F2(dlMiB)} MiB, Written: {F2(writtenMiB)} MiB");
        Err("[DLManager] INFO:  - Cache usage: 96.00 MiB, active tasks: 16");
        Err($"[DLManager] INFO:  + Download\t- {F2(speedMiB)} MiB/s (raw) / {F2(speedMiB * 1.6)} MiB/s (decompressed)");
        Err($"[DLManager] INFO:  + Disk\t- {F2(speedMiB * 1.6)} MiB/s (write) / 0.00 MiB/s (read)");
    }

    /// <summary>cli.py:1240-1346 verify output: CR-updated stdout progress plus stderr results; writes the .repair file.</summary>
    private static void Verify(Dictionary<string, string> kv)
    {
        var app = App(kv);
        var files = int.Parse(kv["files"], CultureInfo.InvariantCulture);
        var interval = int.Parse(kv.GetValueOrDefault("interval", "5"), CultureInfo.InvariantCulture);
        var corrupt = int.Parse(kv.GetValueOrDefault("corrupt", "0"), CultureInfo.InvariantCulture);
        var missing = int.Parse(kv.GetValueOrDefault("missing", "0"), CultureInfo.InvariantCulture);
        var locked = int.Parse(kv.GetValueOrDefault("locked", "0"), CultureInfo.InvariantCulture);
        Err($"[cli] INFO: Loading installed manifest for \"{app}\"");
        Err($"[cli] INFO: Verifying \"{app}\" version \"++Fortnite+Release-33.00-CL-38324456-Windows\"");
        var repair = new List<string>();
        for (var i = 1; i <= files; i++)
        {
            Thread.Sleep(interval);
            var pct = i * 100.0 / files;
            Write(StdOut, $"Verification progress: {i}/{files} ({pct.ToString("F1", CultureInfo.InvariantCulture)}%) [512.3 MiB/s]\t" + (i == files ? "\r\n" : "\r"), ansi: false);
            var path = $"FortniteGame/Content/Paks/pakchunk{i}-WindowsClient.pak";
            if (i <= corrupt)
            {
                Err($"[cli] ERROR: File does not match hash: \"{path}\"");
            }
            else if (i <= corrupt + missing)
            {
                Err($"[cli] ERROR: File is missing: \"{path}\"");
            }
            else if (i <= corrupt + missing + locked)
            {
                Err($"[LFS Utils] CRITICAL: Could not verify \"{path}\"; opening failed with: PermissionError(13, 'The process cannot access the file because it is being used by another process')");
                Err($"[cli] ERROR: Other failure (see log), treating file as missing: \"{path}\"");
            }
            else
            {
                repair.Add($"{Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(path)))}:{path}");
            }
        }

        File.WriteAllText(Path.Combine(_config, "tmp", app + ".repair"), string.Join('\n', repair));
        if (corrupt + missing + locked == 0)
        {
            Err("[cli] INFO: Verification finished successfully.");
        }
        else
        {
            Err($"[cli] ERROR: Verification failed, {corrupt} file(s) corrupted, {missing + locked} file(s) are missing.");
            Err($"[cli] INFO: Run \"legendary repair {app}\" to repair your game installation.");
        }
    }

    private static void Installed(Dictionary<string, string> kv)
    {
        var app = App(kv);
        var file = Path.Combine(_config, "installed.json");
        var root = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))!.AsObject() : [];
        var tags = new JsonArray();
        foreach (var t in kv.GetValueOrDefault("tags", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            tags.Add(t);
        }

        root[app] = new JsonObject
        {
            ["app_name"] = app,
            ["base_urls"] = new JsonArray("https://epicgames-download1.akamaized.net/Builds/Fortnite/CloudDir/ChunksV4"),
            ["can_run_offline"] = false,
            ["egl_guid"] = kv.GetValueOrDefault("eglguid", ""),
            ["executable"] = "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
            ["install_path"] = kv["path"],
            ["install_size"] = long.Parse(kv.GetValueOrDefault("size", "53687091200"), CultureInfo.InvariantCulture),
            ["install_tags"] = tags,
            ["is_dlc"] = false,
            ["launch_parameters"] = "",
            ["manifest_path"] = "",
            ["needs_verification"] = kv.ContainsKey("needsverify"),
            ["platform"] = "Windows",
            ["prereq_info"] = null,
            ["requires_ot"] = false,
            ["save_path"] = null,
            ["title"] = "Fortnite",
            ["uninstaller"] = null,
            ["version"] = kv["version"],
            ["is_preloaded"] = false,
        };
        File.WriteAllText(file, root.ToJsonString());
    }

    private static void Uninstalled(Dictionary<string, string> kv)
    {
        var file = Path.Combine(_config, "installed.json");
        if (File.Exists(file))
        {
            var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            root.Remove(App(kv));
            File.WriteAllText(file, root.ToJsonString());
        }
    }

    private static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private static string Clock(double seconds)
    {
        var s = (long)seconds;
        return $"{s / 3600:D2}:{s % 3600 / 60:D2}:{s % 60:D2}";
    }
}
