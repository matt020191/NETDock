using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// The grid (or list) that opens from a stack. Its folder, view, sort and columns are read
/// from the item every time it opens, and the item is what gets saved, so nothing here can
/// be "forgotten" across restarts. Subfolders open in place with a back button.
/// </summary>
public sealed unsafe class StackFlyout : WindowBase
{
    private readonly App _app;
    private LayeredSurface _surface = null!;
    private nint _nameFormat, _headerFormat, _listFormat;
    private readonly Dictionary<string, nint> _bitmaps = [];

    private DockItem? _item;
    private RECT _anchor;
    private readonly Stack<string> _path = new();
    private List<(string Path, string Name, bool IsDir)> _entries = [];
    private float _s = 1;
    private int _cols, _rows, _visibleRows, _scroll, _hover = -1, _w, _h;
    private float _cellW, _cellH, _icon, _headerH, _margin;
    private bool _created;

    public StackFlyout(App app) => _app = app;

    public DockItem? Item => _item;
    public bool IsOpen => _item is not null;

    private StackOptions Opt => _item!.Stack ??= new StackOptions();
    private bool ListView => Opt.View == StackView.List;

    public void Open(DockItem item, RECT anchor)
    {
        if (!_created)
        {
            Create("NETDock.Stack", Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST, Win32.WS_POPUP, 0, 0, 1, 1, "NETDock stack");
            _surface = new LayeredSurface();
            _app.Icons.AddListener(Hwnd);
            _created = true;
        }
        _item = item;
        _anchor = anchor;
        _path.Clear();
        var root = _app.Tokens.Expand(item.Target) ?? "";
        _path.Push(root);
        Load();
        Win32.ShowWindow(Hwnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(Hwnd);
    }

    public void Close()
    {
        if (_item is null) return;
        _item = null;
        Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
    }

    private void Load()
    {
        var folder = _path.Peek();
        _entries = Enumerate(folder, Opt);
        _scroll = 0;
        _hover = -1;
        Layout();
        Render();
    }

    public static List<(string Path, string Name, bool IsDir)> Enumerate(string folder, StackOptions o)
    {
        var list = new List<(string Path, string Name, bool IsDir, FileSystemInfo Info)>();
        try
        {
            var di = new DirectoryInfo(folder);
            foreach (var fi in di.EnumerateFileSystemInfos())
            {
                var hidden = (fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                if (hidden && !o.ShowHidden) continue;
                if (fi.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var isDir = (fi.Attributes & FileAttributes.Directory) != 0;
                var name = isDir ? fi.Name : Path.GetExtension(fi.Name) is ".lnk" or ".url" ? Path.GetFileNameWithoutExtension(fi.Name) : fi.Name;
                list.Add((fi.FullName, name, isDir, fi));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        IEnumerable<(string Path, string Name, bool IsDir, FileSystemInfo Info)> q = o.SortBy switch
        {
            StackSort.DateModified => list.OrderBy(e => e.Info.LastWriteTimeUtc),
            StackSort.DateCreated => list.OrderBy(e => e.Info.CreationTimeUtc),
            StackSort.Kind => list.OrderBy(e => Path.GetExtension(e.Path), StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var sorted = q.ToList();
        if (o.SortDescending) sorted.Reverse();
        if (o.FoldersFirst) sorted = [.. sorted.Where(e => e.IsDir), .. sorted.Where(e => !e.IsDir)];
        return sorted.Take(o.MaxItems).Select(e => (e.Path, e.Name, e.IsDir)).ToList();
    }

    private void Layout()
    {
        var mon = Monitors.ForPoint((_anchor.Left + _anchor.Right) / 2, (_anchor.Top + _anchor.Bottom) / 2);
        _s = mon.Dpi / 96f;
        _margin = 14 * _s;
        _headerH = 38 * _s;
        var n = Math.Max(1, _entries.Count);
        if (ListView)
        {
            _cols = 1;
            _icon = 24 * _s;
            _cellW = 300 * _s;
            _cellH = 32 * _s;
        }
        else
        {
            _cols = Opt.Columns > 0 ? Opt.Columns : Math.Clamp((int)Math.Ceiling(Math.Sqrt(n * 1.5)), 3, 8);
            _cols = Math.Min(_cols, n);
            _cols = Math.Max(_cols, Math.Min(3, n));
            _icon = 48 * _s;
            _cellW = 92 * _s;
            _cellH = 86 * _s;
        }
        _rows = (n + _cols - 1) / _cols;
        var maxH = (mon.Work.Height * 0.75f) - _headerH - _margin * 2;
        _visibleRows = Math.Max(1, Math.Min(_rows, (int)(maxH / _cellH)));
        _w = (int)(_cols * _cellW + _margin * 2);
        _w = Math.Max(_w, (int)(240 * _s));
        _h = (int)(_headerH + _visibleRows * _cellH + _margin * 1.5f);

        var gap = (int)(10 * _s);
        int x, y;
        switch (_app.Dock.Edge)
        {
            case DockEdge.Top: x = (_anchor.Left + _anchor.Right - _w) / 2; y = _anchor.Bottom + gap; break;
            case DockEdge.Left: x = _anchor.Right + gap; y = (_anchor.Top + _anchor.Bottom - _h) / 2; break;
            case DockEdge.Right: x = _anchor.Left - gap - _w; y = (_anchor.Top + _anchor.Bottom - _h) / 2; break;
            default: x = (_anchor.Left + _anchor.Right - _w) / 2; y = _anchor.Top - gap - _h; break;
        }
        x = Math.Clamp(x, mon.Work.Left + 4, Math.Max(mon.Work.Left + 4, mon.Work.Right - _w - 4));
        y = Math.Clamp(y, mon.Work.Top + 4, Math.Max(mon.Work.Top + 4, mon.Work.Bottom - _h - 4));
        _x = x; _y = y;

        if (_nameFormat != 0) { Com.Release(ref _nameFormat); Com.Release(ref _headerFormat); Com.Release(ref _listFormat); }
        _nameFormat = D2D.CreateTextFormat("Segoe UI", 11.5f * _s, bold: false, centre: true, ellipsis: true);
        _headerFormat = D2D.CreateTextFormat("Segoe UI", 13.5f * _s, bold: true, centre: false, ellipsis: true);
        _listFormat = D2D.CreateTextFormat("Segoe UI", 12.5f * _s, bold: false, centre: false, ellipsis: true);
        _surface.EnsureSize(_w, _h);
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, x, y, _w, _h, Win32.SWP_NOACTIVATE);
    }
    private int _x, _y;

    private (D2DRect Cell, D2DRect Icon, D2DRect Label) CellRects(int index)
    {
        var row = index / _cols - _scroll;
        var col = index % _cols;
        var gridW = _cols * _cellW;
        var left = (_w - gridW) / 2 + col * _cellW;
        var top = _headerH + row * _cellH;
        var cell = new D2DRect(left + 2 * _s, top + 2 * _s, left + _cellW - 2 * _s, top + _cellH - 2 * _s);
        if (ListView)
        {
            var ic = new D2DRect(left + 8 * _s, top + (_cellH - _icon) / 2, left + 8 * _s + _icon, top + (_cellH + _icon) / 2);
            return (cell, ic, new D2DRect(ic.R + 8 * _s, top, left + _cellW - 6 * _s, top + _cellH));
        }
        var icon = new D2DRect(left + (_cellW - _icon) / 2, top + 6 * _s, left + (_cellW + _icon) / 2, top + 6 * _s + _icon);
        return (cell, icon, new D2DRect(left + 4 * _s, icon.B + 4 * _s, left + _cellW - 4 * _s, icon.B + 24 * _s));
    }

    private D2DRect OpenButton => new(_w - _margin - 64 * _s, 8 * _s, _w - _margin, _headerH - 8 * _s);
    private D2DRect BackButton => new(_margin - 4 * _s, 8 * _s, _margin + 24 * _s, _headerH - 8 * _s);

    private void Render()
    {
        if (_item is null) return;
        var s = _surface;
        s.Begin();
        var panel = new D2DRect(0.5f, 0.5f, _w - 0.5f, _h - 0.5f);
        s.FillRoundRect(panel, 12 * _s, new D2DColor(0.07f, 0.09f, 0.13f, 0.92f));
        s.DrawRoundRect(panel, 12 * _s, new D2DColor(1, 1, 1, 0.22f), 1);
        s.DrawLine(new D2DPoint(_margin, _headerH - 1), new D2DPoint(_w - _margin, _headerH - 1), new D2DColor(1, 1, 1, 0.1f), 1);

        var nested = _path.Count > 1;
        var titleLeft = _margin + (nested ? 28 * _s : 0);
        if (nested)
        {
            s.FillRoundRect(BackButton, 6 * _s, new D2DColor(1, 1, 1, _hover == -2 ? 0.18f : 0.08f));
            s.DrawText("‹", _headerFormat, new D2DRect(BackButton.L + 9 * _s, BackButton.T, BackButton.R, BackButton.B), new D2DColor(1, 1, 1, 1));
        }
        var title = nested ? Path.GetFileName(_path.Peek()) : _item.Name;
        s.DrawText(title, _headerFormat, new D2DRect(titleLeft, 0, OpenButton.L - 8 * _s, _headerH), new D2DColor(1, 1, 1, 1));
        s.FillRoundRect(OpenButton, 6 * _s, new D2DColor(1, 1, 1, _hover == -3 ? 0.2f : 0.08f));
        s.DrawText(L.T("Open"), _listFormat, new D2DRect(OpenButton.L + 14 * _s, OpenButton.T, OpenButton.R, OpenButton.B), new D2DColor(1, 1, 1, 0.9f));

        if (_entries.Count == 0)
            s.DrawText(Directory.Exists(_path.Peek()) ? L.T("This folder is empty") : L.T("Folder not found: ") + _path.Peek(),
                _listFormat, new D2DRect(_margin, _headerH, _w - _margin, _h - _margin), new D2DColor(1, 1, 1, 0.6f));

        var first = _scroll * _cols;
        var last = Math.Min(_entries.Count, first + _visibleRows * _cols);
        var size = ListView ? 32 : (int)Math.Min(256, Math.Ceiling(_icon) <= 48 ? 48 : 96);
        for (var i = first; i < last; i++)
        {
            var (cell, icon, label) = CellRects(i);
            if (i == _hover) s.FillRoundRect(cell, 8 * _s, new D2DColor(1, 1, 1, 0.14f));
            var e = _entries[i];
            var key = $"file|{e.Path}|{size}";
            if (!_bitmaps.TryGetValue(key, out var bmp))
            {
                var path = e.Path;
                if (_app.Icons.Get(key, () => Shell.ShellImage(path, size)) is { } img)
                    _bitmaps[key] = bmp = s.CreateBitmap(img.Bgra, img.Width, img.Height);
            }
            if (bmp != 0) s.DrawBitmap(bmp, icon, 1);
            s.DrawText(e.Name, ListView ? _listFormat : _nameFormat, label, new D2DColor(1, 1, 1, 0.95f));
        }
        if (_rows > _visibleRows)
        {
            // Scroll thumb.
            var track = _h - _headerH - _margin;
            var th = track * _visibleRows / _rows;
            var ty = _headerH + (track - th) * _scroll / Math.Max(1, _rows - _visibleRows);
            s.FillRoundRect(new D2DRect(_w - 6 * _s, ty, _w - 3 * _s, ty + th), 2 * _s, new D2DColor(1, 1, 1, 0.3f));
        }
        s.EndAndPresent(Hwnd, _x, _y);
    }

    private int HitTest(int x, int y)
    {
        if (_path.Count > 1 && Contains(BackButton, x, y)) return -2;
        if (Contains(OpenButton, x, y)) return -3;
        var first = _scroll * _cols;
        var last = Math.Min(_entries.Count, first + _visibleRows * _cols);
        for (var i = first; i < last; i++)
            if (Contains(CellRects(i).Cell, x, y)) return i;
        return -1;
    }

    private static bool Contains(D2DRect r, int x, int y) => x >= r.L && x < r.R && y >= r.T && y < r.B;

    private void Click(int hit)
    {
        switch (hit)
        {
            case -2: _path.Pop(); Load(); return;
            case -3: Launcher.Open(_path.Peek()); Close(); return;
            case < 0: return;
        }
        var e = _entries[hit];
        if (e.IsDir) { _path.Push(e.Path); Load(); return; }
        Launcher.Open(e.Path);
        Close();
    }

    protected override nint WndProc(uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case Win32.WM_ACTIVATE:
                if ((w & 0xFFFF) == 0 && !_menuOpen) Close();
                return 0;
            case Win32.WM_MOUSEMOVE:
            {
                var h = HitTest(Win32.LoWord(l), Win32.HiWord(l));
                if (h != _hover) { _hover = h; Render(); }
                return 0;
            }
            case Win32.WM_LBUTTONUP:
                Click(HitTest(Win32.LoWord(l), Win32.HiWord(l)));
                return 0;
            case Win32.WM_RBUTTONUP:
            {
                var h = HitTest(Win32.LoWord(l), Win32.HiWord(l));
                if (h >= 0) EntryMenu(_entries[h].Path);
                return 0;
            }
            case Win32.WM_MOUSEWHEEL:
            {
                var delta = Win32.HiWord(w);
                var max = Math.Max(0, _rows - _visibleRows);
                var next = Math.Clamp(_scroll - Math.Sign(delta), 0, max);
                if (next != _scroll) { _scroll = next; Render(); }
                return 0;
            }
            case Win32.WM_KEYDOWN:
                if (w == 0x1B) Close();
                else if (w == 0x08 && _path.Count > 1) { _path.Pop(); Load(); }
                return 0;
            case IconService.WM_ICONS_READY:
                _app.Icons.Acknowledge();
                Render();
                return 0;
        }
        return Default(msg, w, l);
    }

    private bool _menuOpen;

    private void EntryMenu(string path)
    {
        var m = Win32.CreatePopupMenu();
        Win32.AppendMenu(m, 0, 1, L.T("Open"));
        Win32.AppendMenu(m, 0, 2, L.T("Run as administrator"));
        Win32.AppendMenu(m, 0, 3, L.T("Show in folder"));
        Win32.AppendMenu(m, 0, 4, L.T("Pin to dock"));
        POINT p;
        Win32.GetCursorPos(&p);
        _menuOpen = true;
        var cmd = Win32.TrackPopupMenuEx(m, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON, p.X, p.Y, Hwnd, 0);
        _menuOpen = false;
        Win32.DestroyMenu(m);
        switch (cmd)
        {
            case 1: Launcher.Open(path); Close(); break;
            case 2: Launcher.Open(path, "runas"); Close(); break;
            case 3: Launcher.ShowInExplorer(path); Close(); break;
            case 4:
                if (ItemFactory.FromPath(path, _app.Tokens) is { } item) { Close(); _app.Dock.InsertItem(int.MaxValue, item); }
                break;
        }
    }
}
