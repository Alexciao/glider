using System.Text;
using Glider.Core.Infrastructure;

namespace Glider.Core.Legendary;

/// <summary>
/// Maintains the few <c>[Legendary]</c> keys Glider owns in Legendary's config.ini, preserving everything else verbatim —
/// in particular <c>[Fortnite] install_tags</c>, which Legendary writes after the SDL choice and which <c>verify</c> uses to
/// skip optional files (wiping it would make every verify report the optional packs as missing).
/// Legendary rewrites config.ini at exit and renames it aside if it changed while running (lgndry.py:387-400), so this is
/// only ever written while no Legendary process runs, and only when a managed value actually differs.
/// </summary>
internal static class LegendaryConfigFile
{
    public const string Section = "Legendary";

    public static string PathIn(string configDir) => Path.Combine(configDir, "config.ini");

    /// <summary>
    /// Values Glider enforces. <c>disable_update_check</c> skips Legendary's self-update/API-config fetch inside every login;
    /// Glider refreshes that data through <c>status</c>/<c>auth</c>, which force the fetch anyway (cli.py:112,1610). Until a
    /// version.json exists we leave the check on, because <c>get_sdl_data</c> dereferences the cached API data (core.py:445)
    /// and the SDL config version lives there. <c>egl_sync</c> is pinned off: Glider writes EGL's files itself.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ManagedValues(string configDir)
    {
        var haveApiData = File.Exists(Path.Combine(configDir, "version.json")) &&
                          new FileInfo(Path.Combine(configDir, "version.json")).Length > 2;
        return
        [
            new("disable_update_check", haveApiData ? "true" : "false"),
            new("disable_update_notice", "true"),
            new("egl_sync", "false"),
        ];
    }

    public static async Task<bool> EnsureAsync(string configDir, CancellationToken ct) =>
        await EnsureAsync(configDir, new Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>>(), ct).ConfigureAwait(false);

    /// <summary>The managed <c>[Legendary]</c> keys plus per-game values (section = app name), in one write.</summary>
    public static async Task<bool> EnsureAsync(string configDir, IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, string>>> games, CancellationToken ct)
    {
        Directory.CreateDirectory(configDir);
        var path = PathIn(configDir);
        var bytes = await AtomicFile.TryReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var existing = bytes is null ? null : Encoding.UTF8.GetString(bytes);
        var changed = false;
        var text = existing;
        if (Apply(text, ManagedValues(configDir)) is { } managed)
        {
            (text, changed) = (managed, true);
        }

        foreach (var (app, values) in games)
        {
            if (Apply(text, values, app) is { } updated)
            {
                (text, changed) = (updated, true);
            }
        }

        if (!changed || text is null)
        {
            return false;
        }

        await AtomicFile.WriteAllTextAsync(path, text, keepBackup: false, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Legendary's <c>install_tags</c> value: comma-separated, empty tags dropped (cli.py:971, verify splits on ',').</summary>
    /// <remarks>A tag containing a comma can't be expressed in Legendary's format (it would split into two); such tags are dropped.</remarks>
    public static string InstallTagsValue(IEnumerable<string> tags) =>
        string.Join(",", tags.Select(t => t.Trim()).Where(t => t.Length > 0 && !t.Contains(',', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal));

    /// <summary>Returns the new file content, or null when nothing needs to change. Pure (unit-tested).</summary>
    public static string? Apply(string? existing, IReadOnlyList<KeyValuePair<string, string>> values, string section = Section)
    {
        var text = existing ?? "";
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var want = values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>(lines.Count + values.Count + 2);
        var inSection = false;
        var sectionFound = false;
        var sectionEnd = -1; // index in output after the last non-blank line of our section
        var skipContinuation = false;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (IsHeader(trimmed, out var name))
            {
                if (inSection)
                {
                    sectionEnd = LastContentIndex(output) + 1;
                }

                inSection = name == section;
                sectionFound |= inSection;
                skipContinuation = false;
                output.Add(raw);
                continue;
            }

            if (skipContinuation && raw.Length > 0 && char.IsWhiteSpace(raw[0]) && trimmed.Length > 0)
            {
                continue; // continuation line of a value we replaced
            }

            skipContinuation = false;
            if (inSection && TryKey(raw, out var key) && want.TryGetValue(key, out var value))
            {
                skipContinuation = true;
                if (!seen.Add(key))
                {
                    continue; // duplicate key would put Legendary in read-only safe mode; drop it
                }

                output.Add($"{key} = {value}");
                continue;
            }

            output.Add(raw);
        }

        if (inSection)
        {
            sectionEnd = LastContentIndex(output) + 1;
        }

        var missing = values.Where(v => !seen.Contains(v.Key)).Select(v => $"{v.Key} = {v.Value}").ToList();
        if (!sectionFound)
        {
            var header = new List<string> { $"[{section}]" };
            header.AddRange(missing);
            if (section == Section)
            {
                header.Add("");
                output.InsertRange(0, header); // [Legendary] first, as Legendary writes it
            }
            else
            {
                if (output.Count > 0 && output[^1].Trim().Length > 0)
                {
                    output.Add("");
                }

                output.AddRange(header); // a game's section goes at the end
            }
        }
        else if (missing.Count > 0)
        {
            output.InsertRange(sectionEnd, missing);
        }

        var result = string.Join("\n", output).TrimEnd('\n') + "\n";
        var before = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        return existing is not null && result == before ? null : result;
    }

    private static int LastContentIndex(List<string> output)
    {
        for (var i = output.Count - 1; i >= 0; i--)
        {
            if (output[i].Trim().Length > 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsHeader(string trimmed, out string name)
    {
        if (trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            name = trimmed[1..^1];
            return true;
        }

        name = "";
        return false;
    }

    private static bool TryKey(string raw, out string key)
    {
        key = "";
        if (raw.Length == 0 || char.IsWhiteSpace(raw[0]))
        {
            return false;
        }

        var idx = raw.AsSpan().IndexOfAny('=', ':');
        if (idx <= 0)
        {
            return false;
        }

        key = raw[..idx].Trim();
        return key.Length > 0;
    }
}
