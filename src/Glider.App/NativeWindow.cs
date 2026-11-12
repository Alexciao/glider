using System.ComponentModel;
using System.Runtime.InteropServices;
using Glider.App.Native;

namespace Glider.App;

/// <summary>
/// A bare Win32 window (tray message window, hit-test overlays) with an instance-bound WndProc.
/// No WPF involved, so each costs a few KB. Must be created, used and disposed on one thread.
/// </summary>
internal abstract unsafe class NativeWindow : IDisposable
{
    private static readonly HashSet<string> Registered = [];
    private GCHandle _self;

    public nint Handle { get; private set; }

    protected void CreateWindow(string className, uint style, uint exStyle, nint parent, int x, int y, int width, int height, string? title = null)
    {
        EnsureClass(className);
        _self = GCHandle.Alloc(this);
        Handle = Win32.CreateWindowEx(exStyle, className, title, style, x, y, width, height, parent, 0, Win32.GetModuleHandle(null), GCHandle.ToIntPtr(_self));
        if (Handle == 0)
        {
            var err = Marshal.GetLastWin32Error();
            _self.Free();
            throw new Win32Exception(err, $"CreateWindowEx({className}) failed");
        }
    }

    protected virtual nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam) => Win32.DefWindowProc(hwnd, msg, wParam, lParam);

    protected virtual void OnDestroyed()
    {
    }

    private static void EnsureClass(string className)
    {
        lock (Registered)
        {
            if (Registered.Contains(className))
            {
                return;
            }

            // Class name memory must outlive the class; one small allocation per class for the process lifetime.
            var name = (char*)Marshal.StringToHGlobalUni(className);
            var wc = new WNDCLASSEXW
            {
                Size = (uint)sizeof(WNDCLASSEXW),
                WndProc = &StaticProc,
                Instance = Win32.GetModuleHandle(null),
                ClassName = name,
            };
            if (Win32.RegisterClassEx(in wc) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"RegisterClassEx({className}) failed");
            }

            Registered.Add(className);
        }
    }

    [UnmanagedCallersOnly]
    private static nint StaticProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == Win32.WM_NCCREATE)
            {
                var cs = (CREATESTRUCTW*)lParam;
                Win32.SetWindowLongPtr(hwnd, Win32.GWLP_USERDATA, cs->CreateParams);
            }

            var data = Win32.GetWindowLongPtr(hwnd, Win32.GWLP_USERDATA);
            if (data != 0 && GCHandle.FromIntPtr(data).Target is NativeWindow self)
            {
                var result = self.WndProc(hwnd, msg, wParam, lParam);
                if (msg == Win32.WM_NCDESTROY)
                {
                    Win32.SetWindowLongPtr(hwnd, Win32.GWLP_USERDATA, 0);
                    self.Handle = 0;
                    self.OnDestroyed();
                    if (self._self.IsAllocated)
                    {
                        self._self.Free();
                    }
                }

                return result;
            }
        }
        catch (Exception ex)
        {
            HostLog.Error("WndProc threw", ex);
        }

        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        var h = Handle;
        if (h != 0)
        {
            Win32.DestroyWindow(h);
        }

        GC.SuppressFinalize(this);
    }
}
