namespace Glider.Core.Art;

/// <summary>What the host's decoder learned about an image: its size and a flat colour close to its average.</summary>
public sealed record ImageFacts(int Width, int Height, string DominantColor);

/// <summary>
/// Optional image decoding done by the host (WPF's built-in codecs on Windows; Core has no imaging dependency). Without it the
/// cache stores what the CDN delivered and tiles keep a generated placeholder colour.
/// </summary>
public interface IImageProcessor
{
    /// <summary>Decodes just enough to size an image and average its colour; null when it can't be decoded.</summary>
    ImageFacts? Analyze(ReadOnlySpan<byte> image);

    /// <summary>Scales an image down to <paramref name="maxWidth"/> pixels wide (never up) and returns it as JPEG; null on failure.</summary>
    byte[]? Downscale(ReadOnlySpan<byte> image, int maxWidth);
}

/// <summary>Content type from magic bytes, so a CDN error page or a redirect to HTML is never stored as art.</summary>
public static class ImageSniffer
{
    public static bool TryGetKind(ReadOnlySpan<byte> data, out string contentType, out string extension)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            (contentType, extension) = ("image/jpeg", "jpg");
            return true;
        }

        if (data.Length >= 8 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            (contentType, extension) = ("image/png", "png");
            return true;
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            (contentType, extension) = ("image/webp", "webp");
            return true;
        }

        contentType = extension = "";
        return false;
    }

    public static string? ContentTypeOfExtension(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        "webp" => "image/webp",
        _ => null,
    };
}

/// <summary>Pure pixel math shared by host image processors.</summary>
public static class PixelMath
{
    /// <summary>
    /// A flat, calm colour for blur-up: the mean of the opaque pixels, with saturation and lightness pulled toward the middle so a
    /// very bright or very dark cover still leaves room for white text and the page's theme (the placeholder sits behind the art).
    /// <paramref name="bgra"/> is 4 bytes per pixel (WPF Bgra32 / Pbgra32 order).
    /// </summary>
    public static string DominantColor(ReadOnlySpan<byte> bgra)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128)
            {
                continue;
            }

            b += bgra[i];
            g += bgra[i + 1];
            r += bgra[i + 2];
            n++;
        }

        if (n == 0)
        {
            return "#1c1c22";
        }

        return Soften((int)(r / n), (int)(g / n), (int)(b / n));
    }

    internal static string Soften(int r, int g, int b)
    {
        // Convert to HSL, clamp lightness to [0.18, 0.45], and saturation to <= 0.55, back to RGB.
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var l = (max + min) / 2;
        double h = 0, s = 0;
        if (max - min > 1e-9)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == rf)
            {
                h = ((gf - bf) / d + (gf < bf ? 6 : 0)) / 6;
            }
            else if (max == gf)
            {
                h = ((bf - rf) / d + 2) / 6;
            }
            else
            {
                h = ((rf - gf) / d + 4) / 6;
            }
        }

        s = Math.Min(s, 0.55);
        l = Math.Clamp(l, 0.18, 0.45);
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        static double Channel(double p, double q, double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }

        var (ro, go, bo) = s <= 1e-9
            ? ((int)Math.Round(l * 255), (int)Math.Round(l * 255), (int)Math.Round(l * 255))
            : ((int)Math.Round(Channel(p, q, h + 1.0 / 3) * 255), (int)Math.Round(Channel(p, q, h) * 255), (int)Math.Round(Channel(p, q, h - 1.0 / 3) * 255));
        return $"#{ro:x2}{go:x2}{bo:x2}";
    }
}
