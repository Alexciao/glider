using System.Globalization;
using System.Text;

namespace Glider.Core.Legendary;

/// <summary>
/// Legendary's stream encodings (legendary.md §0.3): stdout is UTF-8 when piped (cli.py:2721); stderr is the
/// Windows ANSI code page with Python's <c>backslashreplace</c> escapes for characters the code page can't encode
/// (e.g. <c>D:\u65e5</c> for 日). PYTHONUTF8 has no effect on the PyInstaller build.
/// </summary>
internal static class LegendaryEncoding
{
    private static readonly Lazy<Encoding> Ansi = new(CreateAnsi);

    public static Encoding Stdout { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static Encoding Stderr => Ansi.Value;

    /// <summary>
    /// Reverses Python's backslashreplace in place and returns the new length. Only escapes Python could have produced are
    /// decoded: <c>\uXXXX</c> for a BMP char the code page can't encode, <c>\UXXXXXXXX</c> above the BMP. <c>\xXX</c> is left
    /// alone on purpose: Python only emits it for C1 controls, while literal paths like <c>D:\x86\</c> are common.
    /// </summary>
    public static int UnescapeBackslashReplace(Span<char> text, Encoding codePage)
    {
        var first = text.IndexOf('\\');
        if (first < 0)
        {
            return text.Length;
        }

        var w = first;
        var r = first;
        Span<char> pair = stackalloc char[2];
        while (r < text.Length)
        {
            var c = text[r];
            if (c == '\\' && r + 1 < text.Length)
            {
                if (text[r + 1] == 'u' && r + 6 <= text.Length && TryHex(text.Slice(r + 2, 4), out var bmp) && bmp >= 0x80 &&
                    !char.IsSurrogate((char)bmp) && !IsEncodable((char)bmp, codePage))
                {
                    text[w++] = (char)bmp;
                    r += 6;
                    continue;
                }

                if (text[r + 1] == 'U' && r + 10 <= text.Length && TryHex(text.Slice(r + 2, 8), out var astral) &&
                    astral is > 0xFFFF and <= 0x10FFFF)
                {
                    new Rune(astral).EncodeToUtf16(pair);
                    text[w++] = pair[0];
                    text[w++] = pair[1];
                    r += 10;
                    continue;
                }
            }

            text[w++] = c;
            r++;
        }

        return w;
    }

    private static bool TryHex(ReadOnlySpan<char> digits, out int value) =>
        int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    private static bool IsEncodable(char c, Encoding codePage)
    {
        if (codePage is UTF8Encoding)
        {
            return true;
        }

        Span<char> one = [c];
        Span<byte> bytes = stackalloc byte[8];
        Span<char> back = stackalloc char[4];
        var n = codePage.GetBytes(one, bytes);
        var m = codePage.GetChars(bytes[..n], back);
        return m == 1 && back[0] == c;
    }

    private static Encoding CreateAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var codePage = OperatingSystem.IsWindows() ? (int)NativeMethods.GetACP() : 1252;
        try
        {
            return Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
    }
}
