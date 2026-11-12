namespace Glider.App;

// Pure window-chrome decisions (no Win32/WPF types) so they can be unit tested: tests/Glider.Core.Tests links this file.

/// <summary>What the window should look like right now. <see cref="Mica"/> = ask DWM for Mica and keep every surface transparent.
/// <see cref="TranslucentPage"/> = the page may let that backdrop show through (otherwise it paints an opaque theme tint).</summary>
public readonly record struct BackdropDecision(bool Mica, bool TranslucentPage);

public static class BackdropPolicy
{
    /// <summary>
    /// Windows deliberately swaps Mica for a flat fallback colour when the window is inactive, when "Transparency effects" is off
    /// and in Battery Saver. We never leave that fallback to chance: without Mica the host and page paint the theme's solid tint
    /// themselves; while Mica is on but the window is inactive the page goes opaque so it matches what DWM shows underneath.
    /// </summary>
    public static BackdropDecision Decide(bool supportsMica, bool transparencyEnabled, bool batterySaver, bool windowActive)
    {
        var mica = supportsMica && transparencyEnabled && !batterySaver;
        return new BackdropDecision(mica, mica && windowActive);
    }
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public enum TopEdgeZone { Top, TopLeft, TopRight }

public static class CaptionGeometry
{
    /// <summary>CSS px to device px at <paramref name="dpi"/> (0 is treated as 96).</summary>
    public static int Scale(int css, int dpi) => (int)Math.Round(css * (dpi <= 0 ? 96 : dpi) / 96.0, MidpointRounding.AwayFromZero);

    /// <summary>The native hit-test rectangle laid over the HTML maximize button, in parent client coordinates.</summary>
    public static PixelRect MaxButton(int clientWidth, int dpi, int rightInsetCss, int widthCss, int heightCss)
    {
        // Round the two edges, not the width: at fractional scales (125/175 %) rounding each term separately drifts the overlay
        // a pixel off the button's own pixel-snapped edges.
        var left = clientWidth - Scale(rightInsetCss + widthCss, dpi);
        var right = clientWidth - Scale(rightInsetCss, dpi);
        return new PixelRect(left, 0, right - left, Scale(heightCss, dpi));
    }

    /// <summary>The thin strip along the top edge that provides top/top-corner resizing.</summary>
    public static PixelRect TopStrip(int clientWidth, int dpi, int thicknessCss) => new(0, 0, clientWidth, Math.Max(1, Scale(thicknessCss, dpi)));

    public static TopEdgeZone TopEdge(int x, int width, int cornerWidth) =>
        x < cornerWidth ? TopEdgeZone.TopLeft : x >= width - cornerWidth ? TopEdgeZone.TopRight : TopEdgeZone.Top;
}
