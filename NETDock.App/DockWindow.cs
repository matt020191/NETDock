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
    private float _base, _max, _pad, _spacing, _sepSpacing, _shelfV0, _shelfThick, _iconV0, _labelH;

    // Panel style: how far the background reaches past the icons (along the row, at the
    // screen-edge side, and on the side away from the edge), in pixels.
    private float _outStart, _outEnd, _outEdge, _outAway;
    private Skin? _skin;
    private nint _skinBg, _skinSep;
    private int _skinBgW, _skinBgH, _skinSepW, _skinSepH, _skinL, _skinR;
    private bool Panel => Opt.Style == DockStyle.Panel;

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
        RecycleBinService.Start(Hwnd);
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
        _sepSpacing = Opt.SeparatorSpacing * _scale;
        _shelfV0 = Opt.EdgeOffset * _scale;
        _shelfThick = _base * 0.45f + _pad;
        _iconV0 = _shelfV0 + _pad * 1.4f;
        LoadSkin();
        if (Panel)
        {
            if (_skin is { } k)
                (_outStart, _outEnd, _outEdge, _outAway) = (k.OutLeft * _scale, k.OutRight * _scale, k.OutBottom * _scale, k.OutTop * _scale);
            else
                (_outStart, _outEnd, _outEdge, _outAway) = (8 * _scale, 8 * _scale, 7 * _scale, 7 * _scale);
            // Padding grows the panel on both sides of the icons rather than moving them off the rim.
            _outEdge += Opt.PanelPadding * _scale;
            _outAway += Opt.PanelPadding * _scale;
            _iconV0 = _shelfV0 + _outEdge;
        }
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

    /// <summary>A separator's own width is just its line (or skin image); the room around it is <see cref="Gap"/>.</summary>
    private float RestLength(DockItem i) => i.Kind != ItemKind.Separator ? _base
        : Panel && _skinSep != 0 ? Math.Max(1, _skinSepW * _scale) : 2 * _scale;

    /// <summary>The gap before item <paramref name="i"/>: separator spacing next to a separator, icon spacing otherwise.</summary>
    private float Gap(int i) =>
        i <= 0 || i >= Items.Count ? 0
        : Items[i].Kind == ItemKind.Separator || Items[i - 1].Kind == ItemKind.Separator ? _sepSpacing : _spacing;

    private void LoadSkin()
    {
        Com.Release(ref _skinBg);
        Com.Release(ref _skinSep);
        _skin = null;
        if (!Panel || _app.Store.ResolveSkin(_app.Tokens.Expand(Opt.Skin)) is not { } folder) return;
        try
        {
            _skin = Skin.Load(folder, f => File.Exists(f) ? File.ReadAllText(f) : null);
            if (_skin is null) return;
            (_skinBg, _skinBgW, _skinBgH) = LoadImage(_skin.Background, out var bgPixels);
            if (bgPixels is not null) (_skinL, _, _skinR, _) = Skin.StretchMargins(bgPixels, _skinBgW, _skinBgH, _skin.Left, _skin.Top, _skin.Right, _skin.Bottom);
            if (_skin.Separator is { } sep) (_skinSep, _skinSepW, _skinSepH) = LoadImage(sep);
            if (_skinBg == 0) _skin = null;
        }
        catch (Exception ex)
        {
            // A broken skin must never stop the dock starting: fall back to the built-in panel.
            App.Log(ex);
            Com.Release(ref _skinBg);
            Com.Release(ref _skinSep);
            _skin = null;
        }
    }

    /// <summary>A skin image at its native size (the Shell thumbnailer decodes the PNG).</summary>
    private (nint Bitmap, int W, int H) LoadImage(string file) => LoadImage(file, out _);

    private (nint Bitmap, int W, int H) LoadImage(string file, out byte[]? pixels)
    {
        pixels = null;
        if (!File.Exists(file)) return (0, 0, 0);
        var head = new byte[24];
        using (var fs = File.OpenRead(file)) fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false); // short file: PngSize returns null
        var native = Skin.PngSize(head);
        var img = Shell.ShellImage(file, native is { } n ? Math.Max(n.W, n.H) : 256, preferThumbnail: true);
        if (img is null) return (0, 0, 0);
        // Slice margins are in source pixels; use what was actually decoded.
        pixels = img.Bgra;
        return (_surface.CreateBitmap(img.Bgra, img.Width, img.Height), img.Width, img.Height);
    }

    /// <summary>Stretch a 9-slice image over dest: corners fixed, edges and centre stretched.</summary>
    private void NineSlice(nint bmp, int iw, int ih, D2DRect dest, int l, int t, int r, int b)
    {
        float sl = l * _scale, st = t * _scale, sr = r * _scale, sb = b * _scale;
        float[] dx = [dest.L, dest.L + sl, dest.R - sr, dest.R];
        float[] dy = [dest.T, dest.T + st, dest.B - sb, dest.B];
        float[] sx = [0, l, iw - r, iw];
        float[] sy = [0, t, ih - b, ih];
        for (var yi = 0; yi < 3; yi++)
        for (var xi = 0; xi < 3; xi++)
        {
            if (dx[xi + 1] <= dx[xi] || dy[yi + 1] <= dy[yi] || sx[xi + 1] <= sx[xi] || sy[yi + 1] <= sy[yi]) continue;
            _surface.DrawBitmap(bmp, new D2DRect(dx[xi], dy[yi], dx[xi + 1], dy[yi + 1]), 1,
                new D2DRect(sx[xi], sy[yi], sx[xi + 1], sy[yi + 1]));
        }
    }

    /// <summary>The RocketDock look: a background that wraps the icons at rest size.</summary>
    private D2DRect DrawPanel(float rowStart, float rowEnd, float off)
    {
        var r = ToWindow(rowStart - _outStart, rowEnd + _outEnd, _shelfV0 - off, _iconV0 + _base + _outAway - off);
        if (_skin is { } k && _skinBg != 0)
        {
            // Skins are drawn for a bottom dock; like RocketDock, mirror them on the top edge
            // so the lit side still faces away from the screen edge.
            if (Opt.Edge == DockEdge.Top)
            {
                _surface.SetTransform(D2DMatrix.FlipY((r.T + r.B) / 2));
                NineSlice(_skinBg, _skinBgW, _skinBgH, r, _skinL, k.Top, _skinR, k.Bottom);
                _surface.SetTransform(D2DMatrix.Identity);
            }
            else NineSlice(_skinBg, _skinBgW, _skinBgH, r, _skinL, k.Top, _skinR, k.Bottom);
            return r;
        }
        // Built-in: dark smoked glass, lighter at the top, thin light rim (close to CrystalXP).
        var radius = 7 * _scale;
        _surface.FillRoundRect(r, radius, new D2DColor(0.09f, 0.1f, 0.12f, 0.82f));
        LayeredSurface.SetGradientLine(_shelfBrush, new D2DPoint(r.L, r.T), new D2DPoint(r.L, r.B));
        _surface.FillRoundRect(r, radius, default, _shelfBrush, setColor: false);
        _surface.DrawRoundRect(r, radius, new D2DColor(1, 1, 1, 0.45f), 1 * _scale);
        var inner = new D2DRect(r.L + 1.5f * _scale, r.T + 1.5f * _scale, r.R - 1.5f * _scale, r.B - 1.5f * _scale);
        _surface.DrawRoundRect(inner, radius - 1, new D2DColor(0, 0, 0, 0.35f), 1 * _scale);
        return r;
    }

    /// <summary>Target scales for the current cursor, worked out on the un-magnified row.</summary>
    private double[] TargetScales()
    {
        var n = Items.Count;
        var t = new double[n];
        Array.Fill(t, 1.0);
        if (_cursorU is not { } cu || !Opt.Magnify || _max <= _base || _dragging >= 0) return t;
        float total = 0;
        for (var i = 0; i < n; i++) total += RestLength(Items[i]) + Gap(i);
        var start = RowCentre() - total / 2;
        var pitch = _base + _spacing;
        var gain = _max / _base - 1;
        var u = start;
        for (var i = 0; i < n; i++)
        {
            u += Gap(i);
            var len = RestLength(Items[i]);
            if (Items[i].Kind != ItemKind.Separator)
            {
                var d = Math.Abs(cu - (u + len / 2)) / (pitch * Opt.MagnifyRange);
                if (d < 1) t[i] = 1 + gain * (Math.Cos(d * Math.PI) + 1) / 2;
            }
            u += len;
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
            total += _len[i] + Gap(i);
        }
        var start = Math.Clamp(RowCentre() - total / 2, _pad * 2, Math.Max(_pad * 2, _length - total - _pad * 2));
        for (var i = 0; i < n; i++) { start += Gap(i); _u0[i] = start; start += _len[i]; }
    }

    private int HitTest(float u, float v)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (u < _u0[i] - Gap(i) / 2 || u >= _u0[i] + _len[i] + Gap(i + 1) / 2) continue;
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

        if (Panel) DrawPanel(rowStart, rowEnd, off);
        else
        {
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
        }

        var now = Environment.TickCount64;
        var indicator = D2DColor.Parse(Opt.IndicatorColor, 0xFFBFE4FF);
        for (var i = 0; i < n; i++)
        {
            var item = Items[i];
            if (i == _dragging) continue;
            if (item.Kind == ItemKind.Separator)
            {
                var mid = _u0[i] + _len[i] / 2;
                if (Panel)
                {
                    float sv0 = _shelfV0 + _outEdge * 0.4f - off, sv1 = _iconV0 + _base + _outAway * 0.6f - off;
                    if (_skinSep != 0 && _skin is { } k)
                        s.DrawBitmap(_skinSep, ToWindow(_u0[i], _u0[i] + _len[i], sv0 + k.SepBottom * _scale, sv1 - k.SepTop * _scale), 1);
                    else
                        s.FillRect(ToWindow(mid - 0.5f * _scale, mid + 0.5f * _scale, sv0, sv1), new D2DColor(1, 1, 1, 0.3f));
                    continue;
                }
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
            if (!Panel && Opt.Reflections && Opt.Edge == DockEdge.Bottom && bounce < 1)
            {
                // Mirror about the icon's base, clipped to the shelf below it.
                var clip = ToWindow(_u0[i], _u0[i] + _len[i], _shelfV0 - off, v0);
                s.PushClip(clip);
                s.SetTransform(D2DMatrix.FlipY(rect.B));
                s.DrawBitmap(bmp, rect, 0.22f);
                s.SetTransform(D2DMatrix.Identity);
                s.PopClip();
            }
            var opacity = Opt.IconOpacity / 100f;
            if (Opt.ZoomOpaque && _scales[i] > 1.02) opacity += (1 - opacity) * (float)Math.Min(1, (_scales[i] - 1) / 0.25);
            if (i == _down && _dragging < 0) opacity *= 0.7f;
            s.DrawBitmap(bmp, rect, opacity);

            if (Opt.ShowRunningIndicators && IsRunning(item))
            {
                var c = _u0[i] + _len[i] / 2;
                var dv = (Panel ? _iconV0 - 2.5f * _scale : _shelfV0 + _pad * 0.7f) - off; // panel: just inside the rim, under the icon
                var dot = ToWindow(c, c, dv, dv);
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
        if (RecycleBin.Is(item))
            item = new DockItem { Kind = item.Kind, Target = item.Target, Icon = RecycleBin.IconFor(item, RecycleBinService.IsFull) };
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

    /// <summary>Per-tick easing so an icon covers ~95 % of the way in ZoomDurationMs (10 ms ticks).</summary>
    private double ZoomStep => 1 - Math.Pow(0.05, 10.0 / Math.Max(30, Opt.ZoomDurationMs));

    private void Tick()
    {
        var target = TargetScales();
        if (_scales.Length != target.Length) { _scales = new double[target.Length]; Array.Fill(_scales, 1.0); }
        var moving = false;
        for (var i = 0; i < target.Length; i++)
        {
            var d = target[i] - _scales[i];
            if (Math.Abs(d) > 0.002) { _scales[i] += d * ZoomStep; moving = true; }
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
        var switchTo = item.OpenRunning switch
        {
            OpenRunning.SwitchTo => true,
            OpenRunning.NewInstance => false,
            _ => Opt.ActivateIfRunning,
        };
        if (!newInstance && !elevate && switchTo && WindowsFor(item) is { Count: > 0 } wins)
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
            if (u < _u0[i] + _len[i] / 2 + Gap(i + 1) / 2) return i;
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
        var (u, v) = ToEdge(pt.X, pt.Y);
        var count = Win32.DragQueryFile(hdrop, 0xFFFFFFFF, null, 0);
        if (HitTest(u, v) is var target and >= 0 && RecycleBin.Is(Items[target]))
        {
            var paths = new List<string>();
            var pbuf = stackalloc char[2048];
            for (uint i = 0; i < count; i++)
                paths.Add(new string(pbuf, 0, (int)Win32.DragQueryFile(hdrop, i, pbuf, 2048)));
            Win32.DragFinish(hdrop);
            RecycleBinService.Recycle(Hwnd, paths);
            _bounces[Items[target].Id] = Environment.TickCount64;
            Kick();
            return;
        }
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
            case RecycleBinService.WM_BIN_CHANGED:
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
        if (msg == App.WM_COMMAND_LINE) { _app.RunCommandLine(w); return 0; }
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
