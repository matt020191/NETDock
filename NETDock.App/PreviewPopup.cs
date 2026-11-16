using System.Runtime.InteropServices;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// Live window previews (Aero Dock's thumbnails.rs, done with DWM directly): hovering a running app's icon
/// shows a live thumbnail of each of its windows. Click one to switch to it, middle-click to close it.
/// A plain (non-layered) popup, because DWM draws the thumbnails into it.
/// </summary>
public sealed unsafe partial class PreviewPopup : WindowBase
{
    private const int MaxWindows = 6;
    private const nuint PollTimer = 1;

    private readonly App _app;
    private bool _created;
    private nint _font, _bg, _hoverBg;
    private float _s = 1;
    private RECT _anchor;
    private DockEdge _edge;
    private readonly List<Entry> _entries = [];
    private int _hover = -1;

    private sealed class Entry
    {
        public nint Window, Thumb;
        public string Title = "";
        public RECT Cell, Thumbnail, Caption;
        public bool Minimized;
    }

    public PreviewPopup(App app) => _app = app;

    /// <summary>The dock item whose windows are showing, or null when hidden.</summary>
    public DockItem? Item { get; private set; }

    public void Show(DockItem item, IReadOnlyList<nint> windows, RECT anchor, DockEdge edge, float scale)
    {
        if (!_created)
        {
            Create("NETDock.Preview", Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOACTIVATE, Win32.WS_POPUP, 0, 0, 1, 1, "NETDock preview");
            var round = 2; // DWMWCP_ROUND
            Ui.DwmSetWindowAttribute(Hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, &round, sizeof(int));
            _bg = Ui.CreateSolidBrush(Ui.Rgb(32, 34, 38));
            _hoverBg = Ui.CreateSolidBrush(Ui.Rgb(62, 66, 74));
            _created = true;
        }
        if (_font == 0 || Math.Abs(scale - _s) > 0.01f)
        {
            if (_font != 0) Win32.DeleteObject(_font);
            _font = Ui.CreateFont(-(int)(12 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        }
        _s = scale;
        Item = item;
        _anchor = anchor;
        _edge = edge;
        Clear();
        foreach (var w in windows.Take(MaxWindows))
        {
            var e = new Entry { Window = w, Title = Title(w), Minimized = Win32.IsIconic(w) };
            nint thumb;
            e.Thumb = DwmRegisterThumbnail(Hwnd, w, &thumb) == 0 ? thumb : 0;
            _entries.Add(e);
        }
        _hover = -1;
        Layout();
        Win32.ShowWindow(Hwnd, 4 /* SW_SHOWNOACTIVATE */);
        Win32.SetTimer(Hwnd, PollTimer, 120, 0);
    }

    public void Hide()
    {
        if (Item is null) return;
        Item = null;
        Win32.KillTimer(Hwnd, PollTimer);
        Clear();
        Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
    }

    private void Clear()
    {
        foreach (var e in _entries) if (e.Thumb != 0) DwmUnregisterThumbnail(e.Thumb);
        _entries.Clear();
    }

    private int S(float v) => (int)MathF.Round(v * _s);

    /// <summary>Thumbnails in a row (a column for side docks), sized to each window's shape, then placed beside the icon.</summary>
    private void Layout()
    {
        var vertical = _edge is DockEdge.Left or DockEdge.Right;
        int pad = S(10), gap = S(8), captionH = S(22), boxW = S(200), boxH = S(124);
        int x = pad, y = pad, maxW = 0, maxH = 0;
        foreach (var e in _entries)
        {
            var (tw, th) = (boxW, boxH);
            if (e.Thumb != 0 && !e.Minimized && DwmQueryThumbnailSourceSize(e.Thumb, out var src) == 0 && src.cx > 0 && src.cy > 0)
                (tw, th) = PreviewLayout.Fit(src.cx, src.cy, boxW, boxH);
            var cellW = boxW + S(12);
            var cellH = captionH + boxH + S(12);
            e.Cell = new RECT(x, y, x + cellW, y + cellH);
            e.Caption = new RECT(x + S(8), y + S(4), x + cellW - S(8), y + S(4) + captionH);
            var tx = x + (cellW - tw) / 2;
            var ty = e.Caption.Bottom + (boxH - th) / 2 + S(2);
            e.Thumbnail = new RECT(tx, ty, tx + tw, ty + th);
            maxW = Math.Max(maxW, e.Cell.Right);
            maxH = Math.Max(maxH, e.Cell.Bottom);
            if (vertical) y += cellH + gap; else x += cellW + gap;
        }
        int w = maxW + pad, h = maxH + pad;

        // Beside the icon, away from the screen edge, kept on the icon's monitor.
        var mon = Monitors.ForPoint((_anchor.Left + _anchor.Right) / 2, (_anchor.Top + _anchor.Bottom) / 2).Work;
        int cx = (_anchor.Left + _anchor.Right) / 2, cy = (_anchor.Top + _anchor.Bottom) / 2, off = S(14);
        var (px, py) = _edge switch
        {
            DockEdge.Top => (cx - w / 2, _anchor.Bottom + off),
            DockEdge.Left => (_anchor.Right + off, cy - h / 2),
            DockEdge.Right => (_anchor.Left - off - w, cy - h / 2),
            _ => (cx - w / 2, _anchor.Top - off - h),
        };
        px = Math.Clamp(px, mon.Left + 4, Math.Max(mon.Left + 4, mon.Right - w - 4));
        py = Math.Clamp(py, mon.Top + 4, Math.Max(mon.Top + 4, mon.Bottom - h - 4));
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, px, py, w, h, Win32.SWP_NOACTIVATE);

        foreach (var e in _entries)
        {
            if (e.Thumb == 0) continue;
            var p = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 0x1 | 0x8 | 0x10, // RECTDESTINATION | VISIBLE | SOURCECLIENTAREAONLY
                rcDestination = e.Thumbnail,
                fVisible = e.Minimized ? 0 : 1,
                fSourceClientAreaOnly = 0,
            };
            DwmUpdateThumbnailProperties(e.Thumb, &p);
        }
        Ui.InvalidateRect(Hwnd, null, true);
    }

    private static string Title(nint w)
    {
        var buf = stackalloc char[256];
        var n = Win32.GetWindowText(w, buf, 256);
        return new string(buf, 0, Math.Max(0, n));
    }

    private int HitTest(int x, int y)
    {
        for (var i = 0; i < _entries.Count; i++) if (_entries[i].Cell.Contains(x, y)) return i;
        return -1;
    }

    /// <summary>Stay open while the cursor is on the popup, its icon, or the gap between them.</summary>
    private void Poll()
    {
        POINT p;
        Win32.GetCursorPos(&p);
        RECT me;
        GetWindowRect(Hwnd, &me);
        var bridge = new RECT(Math.Min(me.Left, _anchor.Left), Math.Min(me.Top, _anchor.Top), Math.Max(me.Right, _anchor.Right), Math.Max(me.Bottom, _anchor.Bottom));
        var overBridge = _edge is DockEdge.Left or DockEdge.Right
            ? bridge.Contains(p.X, p.Y) && p.Y >= Math.Min(me.Top, _anchor.Top) && (me.Contains(p.X, p.Y) || (p.Y >= _anchor.Top && p.Y < _anchor.Bottom))
            : bridge.Contains(p.X, p.Y) && (me.Contains(p.X, p.Y) || (p.X >= _anchor.Left && p.X < _anchor.Right));
        if (!overBridge) { Hide(); return; }
        // Drop windows that closed while the popup was open.
        if (_entries.RemoveAll(e => !Win32.IsWindow(e.Window) && Unregister(e)) > 0)
        {
            if (_entries.Count == 0) { Hide(); return; }
            Layout();
        }
    }

    private static bool Unregister(Entry e) { if (e.Thumb != 0) DwmUnregisterThumbnail(e.Thumb); return true; }

    protected override nint WndProc(uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case Win32.WM_MOUSEACTIVATE:
                return Win32.MA_NOACTIVATE;
            case 0x000F: // WM_PAINT
            {
                PAINTSTRUCT ps;
                var dc = BeginPaint(Hwnd, &ps);
                RECT r;
                Ui.GetClientRect(Hwnd, &r);
                Ui.FillRect(dc, &r, _bg);
                var old = Win32.SelectObject(dc, _font);
                Ui.SetBkMode(dc, 1);
                for (var i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    if (i == _hover)
                    {
                        var rgn = Ui.CreateRoundRectRgn(e.Cell.Left, e.Cell.Top, e.Cell.Right, e.Cell.Bottom, S(10), S(10));
                        Ui.FillRgn(dc, rgn, _hoverBg);
                        Win32.DeleteObject(rgn);
                    }
                    Ui.SetTextColor(dc, Ui.Rgb(235, 238, 242));
                    var cap = e.Caption;
                    Ui.DrawText(dc, e.Title.Length > 0 ? e.Title : Item?.Name ?? "", -1, &cap, 0x4 | 0x20 | 0x8000 | 0x800); // vcentre, single line, end ellipsis, no prefix
                    if (e.Minimized || e.Thumb == 0)
                    {
                        Ui.SetTextColor(dc, Ui.Rgb(150, 156, 166));
                        var t = e.Thumbnail;
                        Ui.DrawText(dc, e.Minimized ? L.T("Minimised") : L.T("No preview"), -1, &t, 0x1 | 0x4 | 0x20);
                    }
                }
                Win32.SelectObject(dc, old);
                EndPaint(Hwnd, &ps);
                return 0;
            }
            case Win32.WM_MOUSEMOVE:
            {
                var h = HitTest(Win32.LoWord(l), Win32.HiWord(l));
                if (h != _hover) { _hover = h; Ui.InvalidateRect(Hwnd, null, true); }
                return 0;
            }
            case Win32.WM_LBUTTONUP:
            {
                var h = HitTest(Win32.LoWord(l), Win32.HiWord(l));
                if (h >= 0) { var win = _entries[h].Window; Hide(); RunningApps.Activate(win); }
                return 0;
            }
            case Win32.WM_MBUTTONUP:
            {
                var h = HitTest(Win32.LoWord(l), Win32.HiWord(l));
                if (h >= 0) Win32.PostMessage(_entries[h].Window, Win32.WM_CLOSE, 0, 0); // the poll drops it once it's gone
                return 0;
            }
            case Win32.WM_TIMER when (nuint)w == PollTimer:
                Poll();
                return 0;
        }
        return Default(msg, w, l);
    }

    // ---- DWM thumbnails

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [LibraryImport("dwmapi.dll")] private static partial int DwmRegisterThumbnail(nint dest, nint src, nint* thumb);
    [LibraryImport("dwmapi.dll")] private static partial int DwmUnregisterThumbnail(nint thumb);
    [LibraryImport("dwmapi.dll")] private static partial int DwmUpdateThumbnailProperties(nint thumb, DWM_THUMBNAIL_PROPERTIES* p);
    [LibraryImport("dwmapi.dll")] private static partial int DwmQueryThumbnailSourceSize(nint thumb, out SIZE size);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint h, RECT* r);
    [LibraryImport("user32.dll")] private static partial nint BeginPaint(nint h, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EndPaint(nint h, PAINTSTRUCT* ps);
}
