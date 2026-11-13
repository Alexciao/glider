namespace Glider.App;

// Pure (no WPF/Win32 types) so tests/Glider.Core.Tests can link this file.

public readonly record struct PlacedWindow(int X, int Y, int Width, int Height);

/// <summary>Placement and popup rules shared by the Epic login window and its social-login popups.</summary>
public static class LoginWindowLayout
{
    /// <summary>
    /// Outer size (physical pixels) clamped to 90 % of the work area, centered on the given point (the owner's center) or on the
    /// work area, and kept fully inside the work area.
    /// </summary>
    public static PlacedWindow Place(int workLeft, int workTop, int workRight, int workBottom, int outerWidth, int outerHeight, int? centerX = null, int? centerY = null)
    {
        var workWidth = workRight - workLeft;
        var workHeight = workBottom - workTop;
        var w = Math.Min(outerWidth, workWidth * 9 / 10);
        var h = Math.Min(outerHeight, workHeight * 9 / 10);
        var cx = centerX ?? (workLeft + workRight) / 2;
        var cy = centerY ?? (workTop + workBottom) / 2;
        var x = Math.Clamp(cx - w / 2, workLeft, workRight - w);
        var y = Math.Clamp(cy - h / 2, workTop, workBottom - h);
        return new PlacedWindow(x, y, w, h);
    }

    /// <summary>Only web popups: http(s), or the empty page OAuth scripts open first and then navigate. Never file:, javascript:, data:, custom schemes.</summary>
    public static bool IsAllowedPopupUrl(string? url) =>
        string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.UserInfo.Length == 0);
}
