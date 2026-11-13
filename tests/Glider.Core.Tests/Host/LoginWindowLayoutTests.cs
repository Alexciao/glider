using Glider.App;

namespace Glider.Core.Tests.Host;

public class LoginWindowLayoutTests
{
    [Fact]
    public void Centers_on_the_owner_when_there_is_room()
    {
        var p = LoginWindowLayout.Place(0, 0, 1920, 1040, 536, 819, 960, 520);
        Assert.Equal(new PlacedWindow(692, 111, 536, 819), p);
    }

    [Fact]
    public void Centers_on_the_work_area_without_an_owner()
    {
        var p = LoginWindowLayout.Place(100, 50, 2020, 1090, 480, 640);
        Assert.Equal(100 + 1920 / 2 - 240, p.X);
        Assert.Equal(50 + 1040 / 2 - 320, p.Y);
    }

    [Fact]
    public void Is_clamped_to_ninety_percent_of_a_small_work_area()
    {
        var p = LoginWindowLayout.Place(0, 0, 1366, 728, 536, 819);
        Assert.Equal(536, p.Width);
        Assert.Equal(728 * 9 / 10, p.Height);
        Assert.InRange(p.Y, 0, 728 - p.Height);
    }

    [Theory]
    [InlineData(5, 5)]        // owner centered in a corner
    [InlineData(1915, 1035)]
    [InlineData(-4000, 9000)] // owner far off screen
    public void Stays_fully_inside_the_work_area(int cx, int cy)
    {
        var p = LoginWindowLayout.Place(0, 0, 1920, 1040, 536, 819, cx, cy);
        Assert.InRange(p.X, 0, 1920 - p.Width);
        Assert.InRange(p.Y, 0, 1040 - p.Height);
    }

    [Fact]
    public void Works_on_a_secondary_monitor_with_negative_coordinates()
    {
        var p = LoginWindowLayout.Place(-1920, 0, 0, 1040, 480, 640, -960, 500);
        Assert.InRange(p.X, -1920, -480);
        Assert.Equal(-960 - 240, p.X);
    }

    [Theory]
    [InlineData("https://accounts.google.com/o/oauth2/auth?x=1", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("about:blank", true)]
    [InlineData("ABOUT:BLANK", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,<b>x</b>", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("ms-settings:network", false)]
    [InlineData("https://user:pw@example.com/", false)]
    [InlineData("about:srcdoc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Popup_scheme_allowlist(string? url, bool allowed) => Assert.Equal(allowed, LoginWindowLayout.IsAllowedPopupUrl(url));
}
