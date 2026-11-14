using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glider.Core.Art;

namespace Glider.App;

/// <summary>
/// Decoding for the art cache with WPF's in-box codecs (no extra dependency): cover colours and shrinking images the CDN didn't
/// resize. Pixel-decode scaling (<c>DecodePixelWidth</c>) lets the JPEG decoder skip most of the work. Safe on thread-pool threads
/// (every bitmap is frozen).
/// </summary>
internal sealed class WpfImageProcessor : IImageProcessor
{
    public ImageFacts? Analyze(ReadOnlySpan<byte> image)
    {
        try
        {
            var bytes = image.ToArray();
            var (width, height) = SizeOf(bytes);
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var small = Decode(bytes, Math.Min(width, 24));
            var converted = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            return new ImageFacts(width, height, PixelMath.DominantColor(pixels));
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or System.IO.IOException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    public byte[]? Downscale(ReadOnlySpan<byte> image, int maxWidth)
    {
        try
        {
            var bytes = image.ToArray();
            var (width, _) = SizeOf(bytes);
            if (width <= 0)
            {
                return null;
            }

            var scaled = Decode(bytes, Math.Min(width, maxWidth));
            var encoder = new JpegBitmapEncoder { QualityLevel = 84 };
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or System.IO.IOException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            return null;
        }
    }

    private static (int Width, int Height) SizeOf(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static BitmapSource Decode(byte[] bytes, int pixelWidth)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.DecodePixelWidth = Math.Max(1, pixelWidth);
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
