using System.Globalization;
using System.Text.RegularExpressions;

namespace Glider.Core.Egl;

/// <summary>
/// Compares Epic build version strings, e.g. Fortnite's <c>++Fortnite+Release-33.10-CL-12345678-Windows</c> (the generic rule).
/// The changelist (<c>-CL-n</c>) is authoritative when both sides have one; otherwise the numeric components are
/// compared in order (ordinal, component-wise). Anything that can't be ordered safely returns null.
/// </summary>
public static partial class BuildVersion
{
    [GeneratedRegex(@"-CL-(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClRegex();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    /// <summary>Changelist number, or null when the string has none.</summary>
    public static long? Changelist(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        var m = ClRegex().Match(version);
        return m.Success && long.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var cl) ? cl : null;
    }

    /// <summary>
    /// &lt;0 if <paramref name="a"/> is older, 0 if the same build, &gt;0 if newer; null if either is empty/unparsable
    /// or the two can't be ordered (e.g. same changelist but different labels).
    /// </summary>
    public static int? Compare(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return null;
        }

        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var clA = Changelist(a);
        var clB = Changelist(b);
        if (clA is not null && clB is not null)
        {
            if (clA != clB)
            {
                return clA.Value.CompareTo(clB.Value);
            }

            // Same CL, different strings (e.g. a hotfix label or platform suffix): not safely orderable.
            return null;
        }

        // Fallback: component-wise numeric comparison of every number in the string (with CL removed so a
        // CL-less string isn't compared against a changelist).
        var na = Numbers(a);
        var nb = Numbers(b);
        if (na.Count == 0 || nb.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < Math.Min(na.Count, nb.Count); i++)
        {
            var c = na[i].CompareTo(nb[i]);
            if (c != 0)
            {
                return c;
            }
        }

        // Same numeric prefix: longer wins only if the extra components are non-zero ("33.10" == "33.10.0").
        var longer = na.Count > nb.Count ? na : nb;
        var extra = longer.Skip(Math.Min(na.Count, nb.Count)).Any(x => x != 0);
        if (!extra)
        {
            return null; // numerically equal but textually different: ambiguous
        }

        return na.Count > nb.Count ? 1 : -1;
    }

    private static List<decimal> Numbers(string s)
    {
        var withoutCl = ClRegex().Replace(s, "");
        var list = new List<decimal>();
        foreach (Match m in NumberRegex().Matches(withoutCl))
        {
            if (decimal.TryParse(m.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                list.Add(n);
            }
        }

        return list;
    }
}
