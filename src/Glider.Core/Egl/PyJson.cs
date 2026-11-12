using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Glider.Core.Egl;

/// <summary>
/// A JSON value model equal to what Python's <c>json.load</c> produces (dict / list / str / int / float / bool / None),
/// and a writer that emits byte-for-byte what <c>json.dump(obj, f, indent=4, sort_keys=True)</c> writes
/// (CPython defaults: <c>ensure_ascii=True</c>, separators <c>(',', ': ')</c> when indent is set, keys sorted by
/// code point, floats via <c>repr</c>). Legendary writes EGL's <c>.item</c> and <c>.mancpn</c> files this way.
/// </summary>
internal static class PyJson
{
    /// <summary>Python's text-mode file writes on Windows translate "\n" to "\r\n"; EGL only exists on Windows.</summary>
    public const string WindowsNewline = "\r\n";

    public static readonly JsonDocumentOptions TolerantOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    /// <summary>Converts a parsed element into the Python-equivalent model. Duplicate keys: last wins (like Python).</summary>
    public static object? FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    d[p.Name] = FromElement(p.Value);
                }

                return d;
            case JsonValueKind.Array:
                var l = new List<object?>(e.GetArrayLength());
                foreach (var item in e.EnumerateArray())
                {
                    l.Add(FromElement(item));
                }

                return l;
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                return ParseNumber(e.GetRawText());
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static object ParseNumber(string raw)
    {
        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
        {
            return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        var big = BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return big >= long.MinValue && big <= long.MaxValue ? (object)(long)big : big;
    }

    /// <summary>Equivalent of <c>json.dumps(value, indent=4, sort_keys=True)</c> with the given newline.</summary>
    public static string Dump(object? value, string newline = WindowsNewline)
    {
        var sb = new StringBuilder(4096);
        Write(sb, value, 0, newline);
        return sb.ToString();
    }

    public static byte[] DumpBytes(object? value, string newline = WindowsNewline) =>
        // ensure_ascii output is pure ASCII, so UTF-8 == ASCII here.
        Encoding.UTF8.GetBytes(Dump(value, newline));

    private static void Write(StringBuilder sb, object? value, int level, string nl)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long n:
                sb.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            case BigInteger big:
                sb.Append(big.ToString(CultureInfo.InvariantCulture));
                break;
            case double f:
                sb.Append(FloatRepr(f));
                break;
            case IDictionary<string, object?> dict:
                WriteDict(sb, dict, level, nl);
                break;
            case IList<string> strings:
                WriteList(sb, strings.Cast<object?>().ToList(), level, nl);
                break;
            case IList<object?> list:
                WriteList(sb, list, level, nl);
                break;
            default:
                throw new ArgumentException($"Unsupported JSON value type {value.GetType()}", nameof(value));
        }
    }

    private static void WriteDict(StringBuilder sb, IDictionary<string, object?> dict, int level, string nl)
    {
        if (dict.Count == 0)
        {
            sb.Append("{}");
            return;
        }

        sb.Append('{');
        var keys = dict.Keys.ToList();
        keys.Sort(CodePointComparer.Instance);
        var first = true;
        foreach (var k in keys)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(nl).Append(' ', 4 * (level + 1));
            WriteString(sb, k);
            sb.Append(": ");
            Write(sb, dict[k], level + 1, nl);
        }

        sb.Append(nl).Append(' ', 4 * level).Append('}');
    }

    private static void WriteList(StringBuilder sb, IList<object?> list, int level, string nl)
    {
        if (list.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        sb.Append('[');
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(nl).Append(' ', 4 * (level + 1));
            Write(sb, list[i], level + 1, nl);
        }

        sb.Append(nl).Append(' ', 4 * level).Append(']');
    }

    /// <summary>CPython <c>encode_basestring_ascii</c>: escapes <c>"</c>, <c>\</c>, and everything outside 0x20..0x7E.</summary>
    internal static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                    {
                        // Lowercase hex, one \uXXXX per UTF-16 unit (non-BMP => surrogate pair), exactly like CPython.
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    /// <summary>CPython <c>float.__repr__</c> (shortest round-trip, 'r' format rules).</summary>
    internal static string FloatRepr(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "Infinity" : "-Infinity";
        }

        if (d == 0)
        {
            return double.IsNegative(d) ? "-0.0" : "0.0";
        }

        // .NET Core 3.0+ "R" is shortest round-trip, like CPython's dtoa mode 0.
        var r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        var exp = 0;
        var ePos = r.IndexOfAny(['E', 'e']);
        if (ePos >= 0)
        {
            exp = int.Parse(r.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..ePos];
        }

        var dot = r.IndexOf('.', StringComparison.Ordinal);
        var intPart = dot >= 0 ? r[..dot] : r;
        var fracPart = dot >= 0 ? r[(dot + 1)..] : "";
        var digits = intPart + fracPart;
        var decpt = intPart.Length + exp;
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
        {
            lead++;
        }

        digits = digits[lead..];
        decpt -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        var sb = new StringBuilder();
        if (d < 0)
        {
            sb.Append('-');
        }

        if (decpt <= -4 || decpt > 16)
        {
            sb.Append(digits[0]);
            if (digits.Length > 1)
            {
                sb.Append('.').Append(digits, 1, digits.Length - 1);
            }

            var e = decpt - 1;
            sb.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (decpt <= 0)
        {
            sb.Append("0.").Append('0', -decpt).Append(digits);
        }
        else if (decpt >= digits.Length)
        {
            sb.Append(digits).Append('0', decpt - digits.Length).Append(".0");
        }
        else
        {
            sb.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        }

        return sb.ToString();
    }

    /// <summary>Python sorts str keys by Unicode code point (not UTF-16 unit order).</summary>
    internal sealed class CodePointComparer : IComparer<string>
    {
        public static readonly CodePointComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var n = Math.Min(x.Length, y.Length);
            for (var i = 0; i < n; i++)
            {
                if (x[i] != y[i])
                {
                    return CodePointAt(x, i).CompareTo(CodePointAt(y, i));
                }
            }

            return x.Length.CompareTo(y.Length);
        }

        private static int CodePointAt(string s, int i) =>
            char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])
                ? char.ConvertToUtf32(s[i], s[i + 1])
                : s[i];
    }
}
