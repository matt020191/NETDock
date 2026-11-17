using System.Runtime.InteropServices;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// Search overlay (Aero Dock's search, native): a hotkey opens a box in the middle of the screen; typing searches
/// the dock's items, every installed app (desktop and Store) and recent files. Enter opens the selection,
/// Ctrl+Shift+Enter runs it as administrator, Esc or clicking elsewhere closes it.
/// </summary>
public sealed unsafe partial class SearchOverlay : WindowBase
{
    private const int IdEdit = 10, MaxRows = 8;
    public const uint WM_INDEX_READY = Win32.WM_APP + 6;

    private readonly App _app;
    private bool _created;
    private nint _edit, _font, _bigFont, _smallFont, _bg, _rowBg, _selBg, _editBg;
    private float _s = 1;
    private int _w, _rowH, _editH, _pad;
    private List<SearchEntry> _results = [];
    private int _sel;
    private readonly Dictionary<string, nint> _icons = [];

    public SearchOverlay(App app) => _app = app;

    public bool IsOpen => _created && Win32.IsWindowVisible(Hwnd);

    /// <summary>The search box, so the message loop can give it arrow/Enter/Esc first.</summary>
    public nint Edit => _edit;

    public void Toggle() { if (IsOpen) Hide(); else Show(); }

    public void Show()
    {
        POINT p;
        Win32.GetCursorPos(&p);
        var mon = Monitors.ForPoint(p.X, p.Y);
        var scale = mon.Dpi / 96f;
        if (!_created || Math.Abs(scale - _s) > 0.01f) Build(scale);
        _w = S(640);
        Ui.SetWindowText(_edit, "");
        Requery();
        var h = Height();
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, mon.Work.Left + (mon.Work.Width - _w) / 2, mon.Work.Top + (int)(mon.Work.Height * 0.22f), _w, h, Win32.SWP_SHOWWINDOW);
        Win32.SetForegroundWindow(Hwnd);
        Win32.SetFocus(_edit);
        SearchIndex.Refresh(Hwnd, WM_INDEX_READY);
    }

    public void Hide()
    {
        if (!_created) return;
        Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
    }

    private int S(float v) => (int)MathF.Round(v * _s);

    private void Build(float scale)
    {
        _s = scale;
        if (!_created)
        {
            Create("NETDock.Search", Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST, Win32.WS_POPUP, 0, 0, 1, 1, "NETDock search");
            var round = 2;
            Ui.DwmSetWindowAttribute(Hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, &round, sizeof(int));
            _bg = Ui.CreateSolidBrush(Ui.Rgb(30, 32, 36));
            _editBg = Ui.CreateSolidBrush(Ui.Rgb(44, 47, 53));
            _selBg = Ui.CreateSolidBrush(Ui.Rgb(56, 92, 150));
            _rowBg = _bg;
            _app.Icons.AddListener(Hwnd);
            _created = true;
        }
        foreach (var f in new[] { _font, _bigFont, _smallFont }) if (f != 0) Win32.DeleteObject(f);
        _bigFont = Ui.CreateFont(-S(20), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _font = Ui.CreateFont(-S(14), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _smallFont = Ui.CreateFont(-S(11.5f), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _pad = S(14);
        _editH = S(30);
        _rowH = S(48);
        if (_edit != 0) Win32.DestroyWindow(_edit);
        _edit = Win32.CreateWindowEx(0, "EDIT", null, Win32.WS_CHILD | Win32.WS_VISIBLE | Ui.ES_AUTOHSCROLL,
            _pad + S(36), _pad + S(4), S(640) - _pad * 2 - S(40), _editH, Hwnd, IdEdit, Win32.GetModuleHandle(0), 0);
        Win32.SendMessage(_edit, Win32.WM_SETFONT, _bigFont, 1);
        fixed (char* cue = L.T("Search apps and recent files"))
            Win32.SendMessage(_edit, 0x1501 /* EM_SETCUEBANNER */, 1, (nint)cue);
    }

    private int Height() => _pad * 2 + _editH + S(10) + (_results.Count > 0 ? _results.Count * _rowH + S(6) : 0);

    private IEnumerable<SearchEntry> DockEntries() =>
        _app.Settings.Items.Where(i => i.Kind is ItemKind.App or ItemKind.Shortcut or ItemKind.Stack && !SpecialItems.Is(i) && !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new SearchEntry(i.Name, i.Id, SearchKind.Dock));

    private void Requery()
    {
        var q = Layout.Text(_edit).Trim();
        _results = q.Length == 0
            ? [.. SearchIndex.Recent.Take(MaxRows)]
            : SearchRanker.Rank(q, DockEntries().Concat(SearchIndex.Apps).Concat(SearchIndex.Recent), MaxRows);
        _sel = 0;
        if (IsOpen || _created)
        {
            RECT r;
            Ui.GetClientRect(Hwnd, &r);
            var h = Height();
            if (r.Height != h) Win32.SetWindowPos(Hwnd, 0, 0, 0, _w, h, Win32.SWP_NOMOVE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            Ui.InvalidateRect(Hwnd, null, true);
        }
    }

    /// <summary>Arrow keys, Enter and Esc from the search box. True when handled.</summary>
    public bool HandleKey(nint vk)
    {
        switch ((int)vk)
        {
            case 0x1B: Hide(); return true;                                           // Esc
            case 0x28: Move(+1); return true;                                         // Down
            case 0x26: Move(-1); return true;                                         // Up
            case 0x0D:                                                                // Enter
                var elevate = Win32.GetKeyState(0x11) < 0 && Win32.GetKeyState(0x10) < 0;
                Open(_sel, elevate);
                return true;
        }
        return false;
    }

    private void Move(int d)
    {
        if (_results.Count == 0) return;
        _sel = (_sel + d + _results.Count) % _results.Count;
        Ui.InvalidateRect(Hwnd, null, false);
    }

    private void Open(int index, bool elevate)
    {
        if (index < 0 || index >= _results.Count) return;
        var e = _results[index];
        Hide();
        if (e.Kind == SearchKind.Dock)
        {
            _app.Dock.ActivateById(e.Target, elevate);
            return;
        }
        if (elevate) Launcher.Launch(new DockItem { Kind = ItemKind.App, Target = e.Target, Name = e.Name }, _app.Tokens, elevate: true);
        else Launcher.Open(e.Target);
    }

    /// <summary>Right-click on a result: Open, Run as administrator, Pin to dock (apps and files not already on it).</summary>
    private void ShowRowMenu(int index)
    {
        _sel = index;
        Ui.InvalidateRect(Hwnd, null, false);
        var e = _results[index];
        var m = Win32.CreatePopupMenu();
        Win32.AppendMenu(m, 0, 1, L.T("Open"));
        if (e.Kind != SearchKind.Dock)
        {
            Win32.AppendMenu(m, 0, 2, L.T("Run as administrator"));
            Win32.AppendMenu(m, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenu(m, 0, 3, L.T("Pin to dock"));
        }
        POINT p;
        Win32.GetCursorPos(&p);
        var cmd = Win32.TrackPopupMenuEx(m, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON, p.X, p.Y, Hwnd, 0);
        Win32.DestroyMenu(m);
        switch (cmd)
        {
            case 1: Open(index, false); break;
            case 2: Open(index, true); break;
            case 3: Pin(e); break;
        }
    }

    /// <summary>Add a search result to the end of the dock (a Store app as shell:AppsFolder\AUMID).</summary>
    private void Pin(SearchEntry e)
    {
        Hide();
        var item = e.Target.StartsWith(RunningKeys.AppsFolder, StringComparison.OrdinalIgnoreCase)
            ? new DockItem { Kind = ItemKind.App, Name = e.Name, Target = e.Target }
            : ItemFactory.FromPath(e.Target, _app.Tokens);
        if (item is null) return;
        if (item.Kind == ItemKind.App && item.Name.Length == 0) item.Name = e.Name;
        _app.Dock.InsertItem(int.MaxValue, item);
    }

    private RECT RowRect(int i)
    {
        var top = _pad + _editH + S(10) + S(3) + i * _rowH;
        return new RECT(S(6), top, _w - S(6), top + _rowH);
    }

    private nint IconFor(SearchEntry e)
    {
        var target = e.Kind == SearchKind.Dock ? _app.Settings.Items.FirstOrDefault(i => i.Id == e.Target) is { } item
            ? (item.Icon is null ? _app.Tokens.Expand(item.Target) : null) : null : e.Target;
        var size = S(32) <= 32 ? 32 : 48;
        string key;
        Func<IconImage?> load;
        if (e.Kind == SearchKind.Dock && _app.Settings.Items.FirstOrDefault(i => i.Id == e.Target) is { } di)
        {
            key = IconService.KeyFor(di, size);
            load = IconService.ForItem(di, _app.Store, _app.Tokens, size);
        }
        else
        {
            key = $"search|{target}|{size}";
            var t = target ?? "";
            load = () => Shell.AppImage(t, size);
        }
        if (_icons.TryGetValue(key, out var h)) return h;
        if (_app.Icons.Get(key, load) is not { } img) return 0;
        h = Ui.ToHIcon(img);
        _icons[key] = h;
        return h;
    }

    protected override nint WndProc(uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case Win32.WM_ACTIVATE when (w & 0xFFFF) == 0: // WA_INACTIVE: clicked elsewhere
                Hide();
                return 0;
            case Win32.WM_COMMAND when (w & 0xFFFF) == IdEdit && ((w >> 16) & 0xFFFF) == Ui.EN_CHANGE:
                Requery();
                return 0;
            case 0x133: // WM_CTLCOLOREDIT
                Ui.SetTextColor(w, Ui.Rgb(240, 242, 245));
                Ui.SetBkColor(w, Ui.Rgb(30, 32, 36));
                return _bg;
            case WM_INDEX_READY:
            case IconService.WM_ICONS_READY:
                if (msg == IconService.WM_ICONS_READY) _app.Icons.Acknowledge();
                if (IsOpen) { if (msg == WM_INDEX_READY) Requery(); else Ui.InvalidateRect(Hwnd, null, false); }
                return 0;
            case Win32.WM_MOUSEMOVE:
            {
                int y = Win32.HiWord(l);
                for (var i = 0; i < _results.Count; i++)
                    if (RowRect(i).Contains(Win32.LoWord(l), y) && i != _sel) { _sel = i; Ui.InvalidateRect(Hwnd, null, false); }
                return 0;
            }
            case Win32.WM_RBUTTONUP:
                for (var i = 0; i < _results.Count; i++)
                    if (RowRect(i).Contains(Win32.LoWord(l), Win32.HiWord(l))) { ShowRowMenu(i); break; }
                return 0;
            case Win32.WM_LBUTTONUP:
                for (var i = 0; i < _results.Count; i++)
                    if (RowRect(i).Contains(Win32.LoWord(l), Win32.HiWord(l))) { Open(i, false); break; }
                return 0;
            case 0x0014: // WM_ERASEBKGND: painted in WM_PAINT
                return 1;
            case 0x000F: // WM_PAINT
                Paint();
                return 0;
        }
        return Default(msg, w, l);
    }

    private void Paint()
    {
        PAINTSTRUCT ps;
        var dc = BeginPaint(Hwnd, &ps);
        RECT client;
        Ui.GetClientRect(Hwnd, &client);
        // Double-buffer so typing doesn't flicker.
        var mem = CreateCompatibleDC(dc);
        var bmp = CreateCompatibleBitmap(dc, client.Width, client.Height);
        var oldBmp = Win32.SelectObject(mem, bmp);
        Ui.FillRect(mem, &client, _bg);
        Ui.SetBkMode(mem, 1);

        // Magnifier glyph before the box.
        var old = Win32.SelectObject(mem, _bigFont);
        Ui.SetTextColor(mem, Ui.Rgb(150, 156, 166));
        var g = new RECT(_pad, _pad, _pad + S(32), _pad + _editH + S(8));
        Ui.DrawText(mem, "⌕", -1, &g, 0x1 | 0x4 | 0x20);

        for (var i = 0; i < _results.Count; i++)
        {
            var e = _results[i];
            var r = RowRect(i);
            if (i == _sel)
            {
                var rgn = Ui.CreateRoundRectRgn(r.Left, r.Top, r.Right, r.Bottom, S(8), S(8));
                Ui.FillRgn(mem, rgn, _selBg);
                Win32.DeleteObject(rgn);
            }
            var icon = IconFor(e);
            if (icon != 0) Ui.DrawIconEx(mem, r.Left + S(10), r.Top + (_rowH - S(32)) / 2, icon, S(32), S(32), 0, 0, 3);
            Win32.SelectObject(mem, _font);
            Ui.SetTextColor(mem, Ui.Rgb(240, 242, 245));
            var nr = new RECT(r.Left + S(54), r.Top + S(6), r.Right - S(12), r.Top + S(26));
            Ui.DrawText(mem, e.Name, -1, &nr, 0x20 | 0x8000 | 0x800);
            Win32.SelectObject(mem, _smallFont);
            Ui.SetTextColor(mem, i == _sel ? Ui.Rgb(210, 222, 240) : Ui.Rgb(140, 146, 156));
            var dr = new RECT(r.Left + S(54), r.Top + S(26), r.Right - S(12), r.Bottom - S(4));
            var detail = e.Kind switch
            {
                SearchKind.Dock => L.T("On the dock"),
                SearchKind.App => L.T("App"),
                _ => L.F("Recent file, {0}", e.Detail ?? ""),
            };
            Ui.DrawText(mem, detail, -1, &dr, 0x20 | 0x8000 | 0x800);
        }
        Win32.SelectObject(mem, old);

        BitBlt(dc, 0, 0, client.Width, client.Height, mem, 0, 0, 0x00CC0020 /* SRCCOPY */);
        Win32.SelectObject(mem, oldBmp);
        Win32.DeleteObject(bmp);
        DeleteDC(mem);
        EndPaint(Hwnd, &ps);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [LibraryImport("user32.dll")] private static partial nint BeginPaint(nint h, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EndPaint(nint h, PAINTSTRUCT* ps);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint dc, int w, int h);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
}
