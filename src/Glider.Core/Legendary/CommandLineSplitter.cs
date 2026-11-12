using System.Text;

namespace Glider.Core.Legendary;

/// <summary>Splits a user-typed argument string with the Windows <c>CommandLineToArgvW</c> rules (quotes, doubled quotes, backslashes before quotes).</summary>
internal static class CommandLineSplitter
{
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var s = commandLine;
        var i = 0;
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            if (i >= s.Length)
            {
                break;
            }

            sb.Clear();
            var inQuotes = false;
            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\\')
                {
                    var start = i;
                    while (i < s.Length && s[i] == '\\')
                    {
                        i++;
                    }

                    var count = i - start;
                    if (i < s.Length && s[i] == '"')
                    {
                        sb.Append('\\', count / 2);
                        if (count % 2 == 1)
                        {
                            sb.Append('"');
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append('\\', count);
                    }

                    continue;
                }

                if (c == '"')
                {
                    if (inQuotes && i + 1 < s.Length && s[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    i++;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    break;
                }

                sb.Append(c);
                i++;
            }

            result.Add(sb.ToString());
        }

        return result;
    }
}
