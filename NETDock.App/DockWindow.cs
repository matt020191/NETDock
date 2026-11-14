using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// The dock: one layered, topmost, never-activating window along a monitor edge, drawn with
/// Direct2D into a per-pixel-alpha surface. Fully transparent pixels pass clicks through, so
/// the window can span the edge without blocking anything outside the visible dock.
///
/// Geometry is worked out in "edge space": u runs along the edge, v is the distance from the
/// screen edge inwards. <see cref="ToWindow"/> maps that to window pixels for each edge.
/// </summary>
public sealed unsafe partial class DockWindow : WindowBase
{
    private const nuint AnimTimer = 1, RunningTimer = 2, RunningDebounce = 3, HideTimer = 4;
    private readonly App _app;
    private LayeredSurface _surface = null!;
    private nint _labelFormat, _shelfBrush;
    private readonly Dictionary<string, nint> _bitmaps = [];
    private uint _shellHookMsg;

    // layout
    private float _scale = 1;
    private RECT _work;
    private int _thickness, _length;
    private float _base, _max, _pad, _spacing, _shelfV0, _shelfThick, _iconV0, _labelH;

    // state
    private double[] _scales = [];
    private float[] _u0 = [], _len = [];
    private float? _cursorU;
    private float _cursorV;
    private bool _tracking, _animating;
    private int _hover = -1;
    private int _down = -1, _dragging = -1;
    private POINT _downPt;
    private float _dragU, _dragV;
    private readonly Dictionary<string, long> _bounces = [];
    private double _hide; // 0 shown, 1 hidden
    private bool _hideWanted;
    private Dictionary<string, List<nint>> _running = new(StringComparer.OrdinalIgnoreCase);

    public DockWindow(App app) => _app = app;

    private DockOptions Opt => _app.Settings.Dock;
    private List<DockItem> Items => _app.Settings.Items;

    public void Show()
    {
        Create("NETDock.Dock",
            Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_ACCEPTFILES,
            Win32.WS_POPUP, 0, 0, 1, 1, "NETDock");
        _surface = new LayeredSurface();
        _app.Icons.AddListener(Hwnd);
        _shellHookMsg = Win32.RegisterWindowMessage("SHELLHOOK");
        Win32.RegisterShellHookWindow(Hwnd);
        Win32.SetTimer(Hwnd, RunningTimer, 3000, 0); // backstop for windows the hook misses
        Relayout();
        RefreshRunning();
        Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
        Render();
    }

    /// <summary>Recompute everything that depends on settings, monitor or DPI.</summary>
    public void Relayout()
    {
        var mon = Monitors.Pick(Opt.Monitor);
        _work = mon.Work;
        _scale = mon.Dpi / 96f;
        _base = Opt.IconSize * _scale;
        _max = (Opt.Magnify ? Opt.MagnifiedSize : Opt.IconSize) * _scale;
        _pad = 6 * _scale;
        _spacing = Opt.Spacing * _scale;
        _shelfV0 = Opt.EdgeOffset * _scale;
        _shelfThick = _base * 0.45f + _pad;
        _iconV0 = _shelfV0 + _pad * 1.4f;
        _labelH = 24 * _scale;

        if (_labelFormat != 0) Com.Release(ref _labelFormat);
        _labelFormat = D2D.CreateTextFormat("Segoe UI", 12.5f * _scale, bold: true, centre: true, ellipsis: false);
        if (_shelfBrush == 0)
            _shelfBrush = _surface.CreateVerticalGradient([
                new D2DGradientStop { Position = 0, Color = new D2DColor(1, 1, 1, 0.28f) },
                new D2DGradientStop { Position = 0.5f, Color = new D2DColor(1, 1, 1, 0.08f) },
                new D2DGradientStop { Position = 1, Color = new D2DColor(1, 1, 1, 0.02f) },
            ]);

        var vertical = Opt.Edge is DockEdge.Left or DockEdge.Right;
        _thickness = (int)MathF.Ceiling(_iconV0 + _max + (vertical ? 260 * _scale : _labelH + _base * 0.6f + 8 * _scale));
        _length = vertical ? _work.Height : _work.Width;
        var (x, y, w, h) = Opt.Edge switch
        {
            DockEdge.Top => (_work.Left, _work.Top, _work.Width, _thickness),
            DockEdge.Left => (_work.Left, _work.Top, _thickness, _work.Height),
            DockEdge.Right => (_work.Right - _thickness, _work.Top, _thickness, _work.Height),
            _ => (_work.Left, _work.Bottom - _thickness, _work.Width, _thickness),
        };
        _winX = x; _winY = y;
        _surface.EnsureSize(w, h);
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE);
        foreach (var b in _bitmaps.Values) { var p = b; Com.Release(ref p); }
        _bitmaps.Clear();
        _scales = new double[Items.Count];
        Array.Fill(_scales, 1.0);
        Render();
    }
    private int _winX, _winY;

    // ---------------------------------------------------------------- geometry

    private D2DRect ToWindow(float u0, float u1, float v0, float v1) => Opt.Edge switch
    {
        DockEdge.Top => new D2DRect(u0, v0, u1, v1),
        DockEdge.Left => new D2DRect(v0, u0, v1, u1),
        DockEdge.Right => new D2DRect(_thickness - v1, u0, _thickness - v0, u1),
        _ => new D2DRect(u0, _thickness - v1, u1, _thickness - v0),
    };

    private (float U, float V) ToEdge(int x, int y) => Opt.Edge switch
    {
        DockEdge.Top => (x, y),
        DockEdge.Left => (y, x),
        DockEdge.Right => (y, _thickness - x),
        _ => (x, _thickness - y),
    };

    private float RestLength(DockItem i) => i.Kind == ItemKind.Separator ? _base * 0.3f : _base;

    /// <summary>Target scales for the current cursor, worked out on the un-magnified row.</summary>
    private double[] TargetScales()
    {
        var n = Items.Count;
        var t = new double[n];
        Array.Fill(t, 1.0);
        if (_cursorU is not { } cu || !Opt.Magnify || _max <= _base || _dragging >= 0) return t;
        float total = 0;
        for (var i = 0; i < n; i++) total += RestLength(Items[i]) + (i > 0 ? _spacing : 0);
        var start = RowCentre() - total / 2;
        var pitch = _base + _spacing;
        var gain = _max / _base - 1;
        var u = start;
        for (var i = 0; i < n; i++)
        {
            var len = RestLength(Items[i]);
            if (Items[i].Kind != ItemKind.Separator)
            {
                var d = Math.Abs(cu - (u + len / 2)) / (pitch * Opt.MagnifyRange);
                if (d < 1) t[i] = 1 + gain * (Math.Cos(d * Math.PI) + 1) / 2;
            }
            u += len + _spacing;
        }
        return t;
    }

    private float RowCentre() => (float)(Opt.Centre * _length);

    private void LayoutRow()
    {
        var n = Items.Count;
        if (_u0.Length != n) { _u0 = new float[n]; _len = new float[n]; }
        if (_scales.Length != n) { _scales = new double[n]; Array.Fill(_scales, 1.0); }
        float total = 0;
        for (var i = 0; i < n; i++)
        {
            _len[i] = RestLength(Items[i]) * (float)_scales[i];
            total += _len[i] + (i > 0 ? _spacing : 0);
        }
        var start = Math.Clamp(RowCentre() - total / 2, _pad * 2, Math.Max(_pad * 2, _length - total - _pad * 2));
        for (var i = 0; i < n; i++) { _u0[i] = start; start += _len[i] + _spacing; }
    }

    private int HitTest(float u, float v)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (u < _u0[i] - _spacing / 2 || u >= _u0[i] + _len[i] + _spacing / 2) continue;
            if (v >= _shelfV0 - 1 && v <= _iconV0 + _len[i] + _pad) return i;
        }
        return -1;
    }

    private float HideOffset => (float)(_hide * (_iconV0 + _base + _pad * 2));

    // ---------------------------------------------------------------- rendering

    public void Render()
    {
        if (Hwnd == 0) return;
        LayoutRow();
        var s = _surface;
        s.Begin();
        var n = Items.Count;
        var off = HideOffset;
        var rowStart = n > 0 ? _u0[0] : RowCentre() - _base;
        var rowEnd = n > 0 ? _u0[n - 1] + _len[n - 1] : RowCentre() + _base;
        if (n == 0) { rowStart = RowCentre() - _base; rowEnd = RowCentre() + _base; }
        float maxLen = _base;
        for (var i = 0; i < n; i++) maxLen = Math.Max(maxLen, _len[i]);

        // An almost-invisible band over the row keeps the window hit-testable between icons,
        // so the mouse never "falls through" the gaps and makes magnification flicker.
        var catcher = new D2DColor(0, 0, 0, 1.5f / 255f);
        if (_hide > 0.99)
            s.FillRect(ToWindow(rowStart - _pad, rowEnd + _pad, 0, 2 * _scale), catcher);
        else
            s.FillRect(ToWindow(rowStart - _pad * 2, rowEnd + _pad * 2, 0, _iconV0 + maxLen + _pad - off), catcher);

        // Shelf
        var shelfC = D2DColor.Parse(Opt.ShelfColor, 0x70182030);
        var shelf = ToWindow(rowStart - _pad * 2, rowEnd + _pad * 2, _shelfV0 - off, _shelfV0 + _shelfThick - off);
        var radius = 8 * _scale;
        s.FillRoundRect(shelf, radius, shelfC);
        var gradTop = Opt.Edge switch { DockEdge.Bottom => new D2DPoint(0, shelf.T), DockEdge.Top => new D2DPoint(0, shelf.B), DockEdge.Left => new D2DPoint(shelf.R, 0), _ => new D2DPoint(shelf.L, 0) };
        var gradBot = Opt.Edge switch { DockEdge.Bottom => new D2DPoint(0, shelf.B), DockEdge.Top => new D2DPoint(0, shelf.T), DockEdge.Left => new D2DPoint(shelf.L, 0), _ => new D2DPoint(shelf.R, 0) };
        LayeredSurface.SetGradientLine(_shelfBrush, gradTop, gradBot);
        s.FillRoundRect(shelf, radius, default, _shelfBrush, setColor: false);
        s.DrawRoundRect(shelf, radius, D2DColor.Parse(Opt.ShelfBorderColor, 0x90FFFFFF).WithAlpha(0.8f), 1 * _scale);

        var now = Environment.TickCount64;
        var indicator = D2DColor.Parse(Opt.IndicatorColor, 0xFFBFE4FF);
        for (var i = 0; i < n; i++)
        {
            var item = Items[i];
            if (i == _dragging) continue;
            if (item.Kind == ItemKind.Separator)
            {
                var mid = _u0[i] + _len[i] / 2;
                var a = ToWindow(mid - _scale, mid + _scale, _shelfV0 + _pad * 0.5f - off, _shelfV0 + _shelfThick - _pad * 0.5f - off);
                s.FillRect(a, new D2DColor(1, 1, 1, 0.35f));
                continue;
            }
            var bounce = BounceOffset(item.Id, now);
            var v0 = _iconV0 + bounce - off;
            var rect = ToWindow(_u0[i], _u0[i] + _len[i], v0, v0 + _len[i]);
            var bmp = Bitmap(item);
            if (bmp == 0)
            {
                s.FillRoundRect(rect, _len[i] * 0.2f, new D2DColor(1, 1, 1, 0.12f));
                continue;
            }
            if (Opt.Reflections && Opt.Edge == DockEdge.Bottom && bounce < 1)
            {
                // Mirror about the icon's base, clipped to the shelf below it.
                var clip = ToWindow(_u0[i], _u0[i] + _len[i], _shelfV0 - off, v0);
                s.PushClip(clip);
                s.SetTransform(D2DMatrix.FlipY(rect.B));
                s.DrawBitmap(bmp, rect, 0.22f);
                s.SetTransform(D2DMatrix.Identity);
                s.PopClip();
            }
            s.DrawBitmap(bmp, rect, i == _down && _dragging < 0 ? 0.7f : 1f);

            if (Opt.ShowRunningIndicators && IsRunning(item))
            {
                var c = _u0[i] + _len[i] / 2;
                var dot = ToWindow(c, c, _shelfV0 + _pad * 0.7f - off, _shelfV0 + _pad * 0.7f - off);
                s.FillEllipse(dot.L, dot.T, 5 * _scale, 5 * _scale, indicator.WithAlpha(0.25f));
                s.FillEllipse(dot.L, dot.T, 2.2f * _scale, 2.2f * _scale, indicator);
            }
        }

        // Item being dragged follows the cursor.
        if (_dragging >= 0 && _dragging < n && Bitmap(Items[_dragging]) is var db && db != 0)
        {
            var sz = _base * 1.1f;
            var leaving = !Opt.Locked && _dragV > _thickness + _base * 0.25f;
            var r = ToWindow(_dragU - sz / 2, _dragU + sz / 2, _dragV - sz / 2, _dragV + sz / 2);
            s.DrawBitmap(db, r, leaving ? 0.35f : 0.9f);
        }

        // Label for the hovered icon.
        if (Opt.ShowLabels && _hover >= 0 && _hover < n && _dragging < 0 && _hide < 0.01 && Items[_hover].Kind != ItemKind.Separator)
            DrawLabel(_hover, off);

        s.EndAndPresent(Hwnd, _winX, _winY);
    }

    private void DrawLabel(int i, float off)
    {
        var text = Items[i].Name;
        if (string.IsNullOrWhiteSpace(text)) return;
        var w = D2D.MeasureWidth(text, _labelFormat) + 20 * _scale;
        var c = _u0[i] + _len[i] / 2;
        var top = _iconV0 + _len[i] + 6 * _scale - off;
        D2DRect r;
        if (Opt.Edge is DockEdge.Left or DockEdge.Right)
        {
            var mid = c;
            var v0 = top;
            r = Opt.Edge == DockEdge.Left
                ? new D2DRect(v0, mid - _labelH / 2, v0 + w, mid + _labelH / 2)
                : new D2DRect(_thickness - v0 - w, mid - _labelH / 2, _thickness - v0, mid + _labelH / 2);
        }
        else
        {
            var u0 = Math.Clamp(c - w / 2, 2, _length - w - 2);
            r = ToWindow(u0, u0 + w, top, top + _labelH);
        }
        _surface.FillRoundRect(r, _labelH / 2, new D2DColor(0.05f, 0.07f, 0.1f, 0.78f));
        _surface.DrawRoundRect(r, _labelH / 2, new D2DColor(1, 1, 1, 0.18f), 1);
        var shadow = new D2DRect(r.L + 1, r.T + 1, r.R + 1, r.B + 1);
        _surface.DrawText(text, _labelFormat, shadow, new D2DColor(0, 0, 0, 0.6f));
        _surface.DrawText(text, _labelFormat, r, D2DColor.Parse(Opt.LabelColor, 0xFFFFFFFF));
    }

    private float BounceOffset(string id, long now)
    {
        if (!_bounces.TryGetValue(id, out var start)) return 0;
        var t = (now - start) / 1000.0;
        if (t > 1.2) { _bounces.Remove(id); return 0; }
        // Two decaying hops.
        var hop = Math.Abs(Math.Sin(t * Math.PI * 2.5)) * Math.Exp(-t * 2.2);
        return (float)(hop * _base * 0.5);
    }

    private int IconPixelSize()
    {
        var want = (int)MathF.Ceiling(_max);
        foreach (var s in (ReadOnlySpan<int>)[32, 48, 64, 96, 128, 256]) if (s >= want) return s;
        return 256;
    }

    private nint Bitmap(DockItem item)
    {
        var size = IconPixelSize();
        var key = IconService.KeyFor(item, size);
        if (_bitmaps.TryGetValue(key, out var bmp)) return bmp;
        var img = _app.Icons.Get(key, IconService.ForItem(item, _app.Store, _app.Tokens, size));
        if (img is null) return 0;
        bmp = _surface.CreateBitmap(img.Bgra, img.Width, img.Height);
        _bitmaps[key] = bmp;
        return bmp;
    }

    public void ForgetIcon(DockItem item)
    {
        foreach (var k in _bitmaps.Keys.Where(k => k.StartsWith($"item|{item.Icon ?? item.Target}|", StringComparison.Ordinal)).ToList())
        {
            var b = _bitmaps[k]; Com.Release(ref b); _bitmaps.Remove(k);
        }
        _app.Icons.Forget(k => k.StartsWith($"item|{item.Icon ?? item.Target}|", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- animation

    private void Kick()
    {
        if (_animating) return;
        _animating = true;
        Win32.SetTimer(Hwnd, AnimTimer, 10, 0);
    }

    private void Tick()
    {
        var target = TargetScales();
        if (_scales.Length != target.Length) { _scales = new double[target.Length]; Array.Fill(_scales, 1.0); }
        var moving = false;
        for (var i = 0; i < target.Length; i++)
        {
            var d = target[i] - _scales[i];
            if (Math.Abs(d) > 0.002) { _scales[i] += d * 0.32; moving = true; }
            else _scales[i] = target[i];
        }
        var hideTarget = _hideWanted ? 1.0 : 0.0;
        if (Math.Abs(_hide - hideTarget) > 0.01) { _hide += (hideTarget - _hide) * 0.25; moving = true; }
        else _hide = hideTarget;
        if (_bounces.Count > 0 || _dragging >= 0) moving = true;

        if (_cursorU is { } cu) _hover = HitTest(cu, _cursorV);
        Render();
        if (!moving)
        {
            _animating = false;
            Win32.KillTimer(Hwnd, AnimTimer);
        }
    }

    // ---------------------------------------------------------------- running apps

    private void RefreshRunning()
    {
        _running = RunningApps.Snapshot();
        Render();
    }

    private bool IsRunning(DockItem item) => RunningApps.ExeFor(item, _app.Tokens) is { } exe && _running.ContainsKey(exe);

    private List<nint>? WindowsFor(DockItem item) =>
        RunningApps.ExeFor(item, _app.Tokens) is { } exe && _running.TryGetValue(exe, out var w) ? w : null;

    // ---------------------------------------------------------------- input

    private void Activate(int index, bool newInstance, bool elevate)
    {
        var item = Items[index];
        switch (item.Kind)
        {
            case ItemKind.Separator:
                return;
            case ItemKind.Stack:
                if (item.Stack?.View == StackView.Open) Launcher.Open(_app.Tokens.Expand(item.Target)!);
                else _app.ToggleStack(item, IconScreenRect(index));
                return;
        }
        if (!newInstance && !elevate && Opt.ActivateIfRunning && WindowsFor(item) is { Count: > 0 } wins)
        {
            var fg = Win32.GetForegroundWindow();
            if (wins.Contains(fg)) Win32.ShowWindow(fg, Win32.SW_MINIMIZE);
            else RunningApps.Activate(wins[0]);
            return;
        }
        Launcher.Launch(item, _app.Tokens, elevate);
        _bounces[item.Id] = Environment.TickCount64;
        Kick();
        Win32.SetTimer(Hwnd, RunningDebounce, 1500, 0);
    }

    /// <summary>Screen rectangle of an icon at rest, for anchoring a flyout.</summary>
    public RECT IconScreenRect(int index)
    {
        var r = ToWindow(_u0[index], _u0[index] + _len[index], _iconV0, _iconV0 + _len[index]);
        return new RECT(_winX + (int)r.L, _winY + (int)r.T, _winX + (int)r.R, _winY + (int)r.B);
    }

    public DockEdge Edge => Opt.Edge;

    private void OnMouseMove(int x, int y)
    {
        if (!_tracking)
        {
            var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = Win32.TME_LEAVE, hwndTrack = Hwnd };
            Win32.TrackMouseEvent(&tme);
            _tracking = true;
        }
        var (u, v) = ToEdge(x, y);
        _cursorU = u; _cursorV = v;
        Win32.KillTimer(Hwnd, HideTimer);
        if (_hideWanted) { _hideWanted = false; }

        if (_down >= 0 && _dragging < 0 && !Opt.Locked
            && (Math.Abs(x - _downPt.X) > 6 * _scale || Math.Abs(y - _downPt.Y) > 6 * _scale))
            _dragging = _down;
        if (_dragging >= 0)
        {
            _dragU = u; _dragV = v;
            // Live reorder: move the dragged item to the slot under the cursor.
            var target = InsertIndex(u);
            if (target != _dragging && target >= 0 && target < Items.Count)
            {
                var it = Items[_dragging];
                Items.RemoveAt(_dragging);
                Items.Insert(target, it);
                _dragging = _down = target;
            }
        }
        Kick();
    }

    private int InsertIndex(float u)
    {
        for (var i = 0; i < Items.Count; i++)
            if (u < _u0[i] + _len[i] / 2 + _spacing / 2) return i;
        return Items.Count - 1;
    }

    private void OnMouseLeave()
    {
        _tracking = false;
        if (_dragging >= 0) return; // captured; keep going
        _cursorU = null;
        _hover = -1;
        if (Opt.AutoHide) Win32.SetTimer(Hwnd, HideTimer, (uint)Math.Max(1, Opt.AutoHideDelayMs), 0);
        Kick();
    }

    private void OnButtonUp(int x, int y)
    {
        Win32.ReleaseCapture();
        var (u, v) = ToEdge(x, y);
        if (_dragging >= 0)
        {
            var leaving = !Opt.Locked && (v > _thickness + _base * 0.25f || v < -_base);
            if (leaving) Items.RemoveAt(_dragging);
            _dragging = _down = -1;
            _app.Save();
            if (leaving) { _scales = []; }
            _cursorU = null;
            Kick();
            return;
        }
        var hit = HitTest(u, v);
        var was = _down;
        _down = -1;
        if (hit >= 0 && hit == was)
        {
            var shift = Win32.GetKeyState(0x10) < 0;
            var ctrl = Win32.GetKeyState(0x11) < 0;
            Activate(hit, newInstance: shift && !ctrl, elevate: shift && ctrl);
        }
        Kick();
    }

    private void OnDrop(nint hdrop)
    {
        POINT pt;
        DragQueryPoint(hdrop, &pt);
        var (u, _) = ToEdge(pt.X, pt.Y);
        var count = Win32.DragQueryFile(hdrop, 0xFFFFFFFF, null, 0);
        var at = Items.Count == 0 ? 0 : Math.Clamp(InsertIndex(u) + (u > _u0[^1] + _len[^1] / 2 ? 1 : 0), 0, Items.Count);
        var buf = stackalloc char[2048];
        for (uint i = 0; i < count; i++)
        {
            var len = Win32.DragQueryFile(hdrop, i, buf, 2048);
            var path = new string(buf, 0, (int)len);
            if (ItemFactory.FromPath(path, _app.Tokens) is { } item) Items.Insert(Math.Min(at++, Items.Count), item);
        }
        Win32.DragFinish(hdrop);
        _app.Save();
        Relayout();
    }

    [System.Runtime.InteropServices.LibraryImport("shell32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DragQueryPoint(nint drop, POINT* pt);

    private void OnRightClick(int x, int y)
    {
        var (u, v) = ToEdge(x, y);
        var hit = HitTest(u, v);
        POINT p;
        Win32.GetCursorPos(&p);
        Menus.ShowDockMenu(_app, this, hit >= 0 ? Items[hit] : null, hit, p);
    }

    public void InsertItem(int at, DockItem item)
    {
        Items.Insert(Math.Clamp(at, 0, Items.Count), item);
        _app.Save();
        Relayout();
    }

    protected override nint WndProc(uint msg, nint w, nint l)
    {
        if (msg == _shellHookMsg && _shellHookMsg != 0)
        {
            // HSHELL_WINDOWCREATED / DESTROYED / ACTIVATED all mean the running set may have changed.
            Win32.SetTimer(Hwnd, RunningDebounce, 150, 0);
            return 0;
        }
        switch (msg)
        {
            case Win32.WM_MOUSEACTIVATE:
                return Win32.MA_NOACTIVATE;
            case Win32.WM_MOUSEMOVE:
                OnMouseMove(Win32.LoWord(l), Win32.HiWord(l));
                return 0;
            case Win32.WM_MOUSELEAVE:
                OnMouseLeave();
                return 0;
            case Win32.WM_LBUTTONDOWN:
            {
                var (u, v) = ToEdge(Win32.LoWord(l), Win32.HiWord(l));
                _down = HitTest(u, v);
                _downPt = new POINT(Win32.LoWord(l), Win32.HiWord(l));
                if (_down >= 0) Win32.SetCapture(Hwnd);
                Kick();
                return 0;
            }
            case Win32.WM_LBUTTONUP:
                OnButtonUp(Win32.LoWord(l), Win32.HiWord(l));
                return 0;
            case Win32.WM_MBUTTONUP:
            {
                var (u, v) = ToEdge(Win32.LoWord(l), Win32.HiWord(l));
                if (HitTest(u, v) is var i and >= 0) Activate(i, newInstance: true, elevate: false);
                return 0;
            }
            case Win32.WM_CAPTURECHANGED:
                if (_dragging >= 0) { _dragging = _down = -1; _app.Save(); Kick(); }
                return 0;
            case Win32.WM_RBUTTONUP:
                OnRightClick(Win32.LoWord(l), Win32.HiWord(l));
                return 0;
            case Win32.WM_DROPFILES:
                OnDrop(w);
                return 0;
            case Win32.WM_TIMER:
                switch ((nuint)w)
                {
                    case AnimTimer: Tick(); break;
                    case RunningTimer: RefreshRunning(); break;
                    case RunningDebounce: Win32.KillTimer(Hwnd, RunningDebounce); RefreshRunning(); break;
                    case HideTimer:
                        Win32.KillTimer(Hwnd, HideTimer);
                        if (_cursorU is null && !_app.StackOpen) { _hideWanted = true; Kick(); }
                        break;
                }
                return 0;
            case IconService.WM_ICONS_READY:
                _app.Icons.Acknowledge();
                Render();
                return 0;
            case Win32.WM_DISPLAYCHANGE:
            case Win32.WM_DPICHANGED:
                Relayout();
                return 0;
            case Win32.WM_SETTINGCHANGE:
                if (w == 0x2F /* SPI_SETWORKAREA */) Relayout();
                return 0;
            case Win32.WM_DESTROY:
                _app.Icons.RemoveListener(Hwnd);
                Win32.PostQuitMessage(0);
                return 0;
        }
        if (msg == App.WM_TRAY) { _app.OnTray(this, (uint)l); return 0; }
        return Default(msg, w, l);
    }

    public void Close() => Win32.DestroyWindow(Hwnd);
}

public readonly record struct MonitorInfo(RECT Bounds, RECT Work, uint Dpi);

public static unsafe class Monitors
{
    private static readonly List<nint> Found = [];

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int Enum(nint mon, nint dc, RECT* r, nint data) { Found.Add(mon); return 1; }

    public static List<nint> All()
    {
        Found.Clear();
        Win32.EnumDisplayMonitors(0, null, &Enum, 0);
        return [.. Found];
    }

    public static MonitorInfo Pick(int index)
    {
        var all = All();
        var mon = index > 0 && index <= all.Count ? all[index - 1] : Win32.MonitorFromPoint(new POINT(0, 0), 1 /* PRIMARY */);
        return Info(mon);
    }

    public static MonitorInfo Info(nint mon)
    {
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        Win32.GetMonitorInfo(mon, &mi);
        uint dx = 96, dy = 96;
        Win32.GetDpiForMonitor(mon, 0, &dx, &dy);
        return new MonitorInfo(mi.rcMonitor, mi.rcWork, dx);
    }

    public static MonitorInfo ForPoint(int x, int y) => Info(Win32.MonitorFromPoint(new POINT(x, y), 2 /* NEAREST */));
}
