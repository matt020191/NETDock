using System.Runtime.InteropServices;
using NETDock.App.Native;

namespace NETDock.App;

/// <summary>Registers one window class and routes messages to the owning instance.</summary>
public abstract unsafe class WindowBase
{
    private static readonly Dictionary<nint, WindowBase> Live = [];
    private static WindowBase? _creating;
    private static readonly HashSet<string> Registered = [];

    public nint Hwnd { get; private set; }

    protected void Create(string className, uint exStyle, uint style, int x, int y, int w, int h, string? title = null, nint owner = 0)
    {
        if (Registered.Add(className))
        {
            fixed (char* cls = className)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    style = 0x8, // CS_DBLCLKS
                    lpfnWndProc = &StaticProc,
                    hInstance = Win32.GetModuleHandle(0),
                    hCursor = Win32.LoadCursor(0, 32512),
                    hIcon = App.AppIcon,
                    hIconSm = App.AppIcon,
                    hbrBackground = (nint)(15 + 1), // COLOR_BTNFACE + 1
                    lpszClassName = cls,
                };
                Win32.RegisterClassEx(&wc);
            }
        }
        _creating = this;
        Hwnd = Win32.CreateWindowEx(exStyle, className, title, style, x, y, w, h, owner, 0, Win32.GetModuleHandle(0), 0);
        _creating = null;
    }

    protected abstract nint WndProc(uint msg, nint w, nint l);

    protected nint Default(uint msg, nint w, nint l) => Win32.DefWindowProc(Hwnd, msg, w, l);

    [UnmanagedCallersOnly]
    private static nint StaticProc(nint hwnd, uint msg, nint w, nint l)
    {
        if (!Live.TryGetValue(hwnd, out var win))
        {
            if (_creating is null) return Win32.DefWindowProc(hwnd, msg, w, l);
            win = _creating;
            win.Hwnd = hwnd;
            Live[hwnd] = win;
        }
        try
        {
            var r = win.WndProc(msg, w, l);
            if (msg == Win32.WM_NCDESTROY) Live.Remove(hwnd);
            return r;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return Win32.DefWindowProc(hwnd, msg, w, l);
        }
    }
}
