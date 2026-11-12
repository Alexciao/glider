using Glider.App;

namespace Glider.Core.Tests.Host;

public class BackdropPolicyTests
{
    [Fact]
    public void Mica_when_supported_active_and_effects_on()
    {
        var d = BackdropPolicy.Decide(supportsMica: true, transparencyEnabled: true, batterySaver: false, windowActive: true);
        Assert.True(d.Mica);
        Assert.True(d.TranslucentPage);
    }

    [Fact]
    public void Inactive_window_keeps_Mica_but_page_goes_solid()
    {
        // Windows shows its flat fallback for an inactive Mica window: the page paints the matching opaque tint.
        var d = BackdropPolicy.Decide(true, true, false, windowActive: false);
        Assert.True(d.Mica);
        Assert.False(d.TranslucentPage);
    }

    [Theory]
    [InlineData(true, false, false, true)]  // transparency effects off
    [InlineData(true, true, true, true)]    // battery saver
    [InlineData(false, true, false, true)]  // Windows 10 / pre-22621
    [InlineData(false, false, true, false)]
    public void Solid_everywhere_when_Mica_is_unavailable(bool supports, bool transparency, bool saver, bool active)
    {
        var d = BackdropPolicy.Decide(supports, transparency, saver, active);
        Assert.False(d.Mica);
        Assert.False(d.TranslucentPage);
    }
}

public class CaptionGeometryTests
{
    [Theory]
    [InlineData(46, 96, 46)]
    [InlineData(46, 120, 58)]
    [InlineData(46, 144, 69)]
    [InlineData(40, 192, 80)]
    [InlineData(40, 0, 40)] // unknown DPI behaves as 96
    public void Scale_css_to_device_pixels(int css, int dpi, int expected) => Assert.Equal(expected, CaptionGeometry.Scale(css, dpi));

    [Fact]
    public void Max_button_sits_between_min_and_close_at_100_percent()
    {
        var r = CaptionGeometry.MaxButton(clientWidth: 1164, dpi: 96, rightInsetCss: 46, widthCss: 46, heightCss: 40);
        Assert.Equal(new PixelRect(1072, 0, 46, 40), r);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)]
    [InlineData(192)]
    public void Max_button_edges_match_the_pixel_snapped_html_button(int dpi)
    {
        const int width = 1500;
        var r = CaptionGeometry.MaxButton(width, dpi, 46, 46, 40);
        Assert.Equal(width - CaptionGeometry.Scale(46, dpi), r.X + r.Width);
        Assert.Equal(width - CaptionGeometry.Scale(92, dpi), r.X);
        Assert.Equal(CaptionGeometry.Scale(40, dpi), r.Height);
    }

    [Fact]
    public void Top_strip_spans_the_client_and_is_never_zero_high()
    {
        Assert.Equal(new PixelRect(0, 0, 900, 4), CaptionGeometry.TopStrip(900, 96, 4));
        Assert.True(CaptionGeometry.TopStrip(900, 96, 0).Height >= 1);
    }

    [Theory]
    [InlineData(0, TopEdgeZone.TopLeft)]
    [InlineData(13, TopEdgeZone.TopLeft)]
    [InlineData(14, TopEdgeZone.Top)]
    [InlineData(885, TopEdgeZone.Top)]
    [InlineData(886, TopEdgeZone.TopRight)]
    [InlineData(899, TopEdgeZone.TopRight)]
    public void Top_edge_zones(int x, TopEdgeZone expected) => Assert.Equal(expected, CaptionGeometry.TopEdge(x, 900, 14));
}
