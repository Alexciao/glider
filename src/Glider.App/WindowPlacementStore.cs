using System.Globalization;
using System.Text.Json;
using Glider.App.Native;
using Glider.Core;

namespace Glider.App;

/// <summary>Persists the window rectangle + maximized state to state\window.json in the data root (Get/SetWindowPlacement).</summary>
internal static class WindowPlacementStore
{
    private const string FileName = "window.json";
    private const uint WPF_RESTORETOMAXIMIZED = 0x2;

    public static void Save(AppPaths paths, nint hwnd)
    {
        var wp = new WINDOWPLACEMENT { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!Win32.GetWindowPlacement(hwnd, ref wp))
        {
            return;
        }

        var maximized = wp.ShowCmd == Win32.SW_SHOWMAXIMIZED || (wp.ShowCmd == Win32.SW_SHOWMINIMIZED && (wp.Flags & WPF_RESTORETOMAXIMIZED) != 0);
        var r = wp.NormalPosition;
        var json = string.Create(CultureInfo.InvariantCulture,
            $"{{\"version\":1,\"maximized\":{(maximized ? "true" : "false")},\"left\":{r.Left},\"top\":{r.Top},\"right\":{r.Right},\"bottom\":{r.Bottom}}}");
        try
        {
            Directory.CreateDirectory(paths.State);
            var path = Path.Combine(paths.State, FileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HostLog.Warn("Could not save window placement", ex);
        }
    }

    /// <summary>Applies the saved placement (or a centered default) to a window that is already shown (cloaked).</summary>
    public static void Restore(AppPaths paths, nint hwnd)
    {
        var dpi = Win32.GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var wp = new WINDOWPLACEMENT { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>(), ShowCmd = Win32.SW_SHOWNORMAL };
        var saved = TryRead(paths, out var rect, out var maximized);
        if (saved)
        {
            // Must still intersect some monitor, and be a sensible size, or fall back to the default.
            saved = rect.Width >= 400 && rect.Height >= 300 && Win32.MonitorFromRect(in rect, Win32.MONITOR_DEFAULTTONULL) != 0;
        }

        if (!saved)
        {
            var mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            Win32.GetMonitorInfo(mon, ref mi);
            var w = Math.Min((int)(1180L * dpi / 96), mi.Work.Width - 40);
            var h = Math.Min((int)(760L * dpi / 96), mi.Work.Height - 40);
            var x = mi.Work.Left + (mi.Work.Width - w) / 2;
            var y = mi.Work.Top + (mi.Work.Height - h) / 2;
            rect = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            maximized = false;
        }

        wp.NormalPosition = rect;
        wp.ShowCmd = maximized ? (uint)Win32.SW_SHOWMAXIMIZED : (uint)Win32.SW_SHOWNORMAL;
        Win32.SetWindowPlacement(hwnd, in wp);
    }

    private static bool TryRead(AppPaths paths, out RECT rect, out bool maximized)
    {
        rect = default;
        maximized = false;
        try
        {
            var path = Path.Combine(paths.State, FileName);
            if (!File.Exists(path))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            rect = new RECT
            {
                Left = root.GetProperty("left").GetInt32(),
                Top = root.GetProperty("top").GetInt32(),
                Right = root.GetProperty("right").GetInt32(),
                Bottom = root.GetProperty("bottom").GetInt32(),
            };
            maximized = root.TryGetProperty("maximized", out var m) && m.ValueKind == JsonValueKind.True;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or UnauthorizedAccessException)
        {
            HostLog.Warn("Ignoring unreadable window.json", ex);
            return false;
        }
    }
}
