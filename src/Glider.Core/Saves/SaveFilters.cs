using System.Text;
using System.Text.RegularExpressions;

namespace Glider.Core.Saves;

/// <summary>
/// Epic's <c>CloudIncludeList</c> / <c>CloudExcludeList</c> (comma-separated), matched exactly as Legendary's
/// <c>_filename_matches</c> does (savegame_helper.py): a trailing-slash pattern is a folder prefix, a pattern that is a
/// suffix of the relative path matches, otherwise an fnmatch glob (case-insensitive on Windows). Empty patterns are ignored
/// (in Legendary an empty exclude list would exclude everything, which is never what a game means).
/// </summary>
internal sealed class SaveFilters
{
    public static SaveFilters None { get; } = new(null, null);

    private readonly string[]? _include;
    private readonly string[]? _exclude;

    public SaveFilters(IEnumerable<string>? include, IEnumerable<string>? exclude)
    {
        _include = Clean(include);
        _exclude = Clean(exclude);
    }

    public bool IsEmpty => _include is null && _exclude is null;

    public static SaveFilters FromMetadata(string? include, string? exclude) =>
        new(include?.Split(','), exclude?.Split(','));

    /// <summary>Whether a file (path relative to the save folder, any separators) takes part in the sync.</summary>
    public bool Matches(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        if (_include is not null && !MatchesAny(path, _include))
        {
            return false;
        }

        return _exclude is null || !MatchesAny(path, _exclude);
    }

    private static string[]? Clean(IEnumerable<string>? patterns)
    {
        var list = patterns?.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        return list is { Length: > 0 } ? list : null;
    }

    private static bool MatchesAny(string path, string[] patterns)
    {
        foreach (var raw in patterns)
        {
            var pattern = raw.Replace('\\', '/');
            if ((pattern.EndsWith('/') && path.StartsWith(pattern, StringComparison.Ordinal))
                || path.EndsWith(pattern, StringComparison.Ordinal)
                || Glob(pattern).IsMatch(path))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Dictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>Python's <c>fnmatch.translate</c>: * any run (including /), ? one char, [seq] / [!seq].</summary>
    private static Regex Glob(string pattern)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(pattern, out var cached))
            {
                return cached;
            }

            var sb = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                switch (c)
                {
                    case '*':
                        sb.Append(".*");
                        break;
                    case '?':
                        sb.Append('.');
                        break;
                    case '[':
                        var close = i + 1;
                        if (close < pattern.Length && pattern[close] == '!')
                        {
                            close++;
                        }

                        if (close < pattern.Length && pattern[close] == ']')
                        {
                            close++;
                        }

                        while (close < pattern.Length && pattern[close] != ']')
                        {
                            close++;
                        }

                        if (close >= pattern.Length)
                        {
                            sb.Append("\\[");
                            break;
                        }

                        var body = pattern[(i + 1)..close];
                        var negate = body.StartsWith('!');
                        if (negate)
                        {
                            body = body[1..];
                        }

                        sb.Append(negate ? "[^" : "[").Append(body.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("^", "\\^", StringComparison.Ordinal)).Append(']');
                        i = close;
                        break;
                    default:
                        sb.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }

            sb.Append('$');
            var regex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            Cache[pattern] = regex;
            return regex;
        }
    }
}
