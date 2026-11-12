using System.Security.Cryptography;
using System.Text;

namespace Glider.Core.Egl;

/// <summary>Path comparison the way Windows (and EGL) sees it: case-insensitive, either slash, no trailing separator.</summary>
public static class EglPath
{
    /// <summary>Comparison key only (never used as a real path).</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var p = path.Trim().Replace('\\', '/');
        if (p.StartsWith("//?/", StringComparison.Ordinal) || p.StartsWith("//./", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        // Collapse duplicate separators (keep a leading UNC "//").
        var unc = p.StartsWith("//", StringComparison.Ordinal);
        var sb = new StringBuilder(p.Length);
        foreach (var c in p)
        {
            if (c == '/' && sb.Length > 0 && sb[^1] == '/')
            {
                continue;
            }

            sb.Append(c);
        }

        p = (unc ? "/" : "") + sb.ToString();
        p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p.ToUpperInvariant();
    }

    public static bool Same(string? a, string? b) =>
        Normalize(a).Length > 0 && string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary>Stable id for a location: same path (in any spelling) => same id across scans and restarts.</summary>
    public static string CandidateId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(path)));
        return "egl-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>Removes trailing separators but keeps roots like "D:\" or "/" intact.</summary>
    public static string TrimTrailingSeparators(string path)
    {
        var p = path.Trim();
        while (p.Length > 1 && (p[^1] == '\\' || p[^1] == '/') && !(p.Length == 3 && p[1] == ':'))
        {
            p = p[..^1];
        }

        return p;
    }
}
