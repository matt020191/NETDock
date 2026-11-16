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
    private const nuint AnimTimer = 1, RunningTimer = 2, RunningDebounce = 3, HideTimer = 4, PopTimer = 5, PreviewTimer = 6, VolumeTimer = 7, TileTimer = 8, AmbientTimer = 9, UpdateTimer = 10;
    private D2DColor? _accent;
    private readonly App _app;
    private LayeredSurface _surface = null!;
    private nint _labelFormat, _shelfBrush;
    private readonly Dictionary<string, nint> _bitmaps = [];
    private uint _shellHookMsg;

    // layout
    private float _scale = 1;
    private RECT _work;
    private int _thickness, _length;
    /// <summary>Icon size from settings; <c>_base</c> is that or smaller when the row would run off the screen.</summary>
    private float _baseFull;
    private float _base, _max, _pad, _spacing, _sepSpacing, _shelfV0, _shelfThick, _iconV0, _labelH;

    // Panel style: how far the background reaches past the icons (along the row, at the
    // screen-edge side, and on the side away from the edge), in pixels.
    private float _outStart, _outEnd, _outEdge, _outAway;
    private Skin? _skin;
    private nint _skinBg, _skinSep, _skinTinted;
    private byte[]? _skinPixels;
    private D2DColor? _skinTintFor;
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
    /// <summary>This drag may reorder/remove: icons unlocked, or Ctrl held when it started.</summary>
    private bool _dragUnlocked;
    /// <summary>Running icon effects: item id -> (start tick, effect). Attention ones loop while the id is in <see cref="_attention"/>.</summary>
    private readonly Dictionary<string, (long Start, IconEffect Effect)> _effects = [];
    /// <summary>Exe path (lower-case) per item id whose window is flashing for attention.</summary>
    private readonly Dictionary<string, string> _attention = [];
    private double _hide; // 0 shown, 1 hidden
    private bool _hideWanted;
    /// <summary>Linear slide progress (0 shown, 1 hidden); <c>_hide</c> is its eased value.</summary>
    private double _hideP;
    private long _lastTick;
    private Dictionary<string, List<nint>> _running = new(StringComparer.OrdinalIgnoreCase);

    public DockWindow(App app) => _app = app;

    private DockOptions Opt => _app.Settings.Dock;
    /// <summary>The saved items. All edits (reorder, insert, remove) go here.</summary>
    private List<DockItem> Pinned => _app.Settings.Items;
    /// <summary>What's on the dock: the saved items, then minimised windows after a separator.</summary>
    private List<DockItem> Items => _view;
    private List<DockItem> _view = [];
    private readonly List<(nint Hwnd, DockItem Item)> _minimized = [];
    private static readonly DockItem MinimizedSeparator = new() { Id = "netdock-minimized-separator", Kind = ItemKind.Separator };

    private static readonly DockItem RunningSeparator = new() { Id = "netdock-running-separator", Kind = ItemKind.Separator };
    /// <summary>Running programs that aren't pinned (when <see cref="DockOptions.ShowRunningApps"/> is on), in order of appearance.</summary>
    private readonly List<DockItem> _unpinned = [];

    private void SyncView()
    {
        if (_minimized.Count == 0 && _unpinned.Count == 0) { if (_view.Count != Pinned.Count || !_view.SequenceEqual(Pinned)) _view = [.. Pinned]; return; }
        var v = new List<DockItem>(Pinned);
        if (_unpinned.Count > 0) { v.Add(RunningSeparator); v.AddRange(_unpinned); }
        if (_minimized.Count > 0) { v.Add(MinimizedSeparator); v.AddRange(_minimized.Select(m => m.Item)); }
        _view = v;
    }

    /// <summary>Is this one of the "running, not pinned" icons?</summary>
    public bool IsUnpinned(DockItem item) => _unpinned.Contains(item);

    /// <summary>Track running programs with no pinned icon. True when the set changed.</summary>
    private bool UpdateUnpinned()
    {
        var before = _unpinned.Select(i => i.Target).ToList();
        if (!Opt.ShowRunningApps) { _unpinned.Clear(); return before.Count > 0; }
        // Windows already shown by a pinned icon (by any key: exe, launcher, AUMID) don't make an extra icon.
        var pinnedWindows = new HashSet<nint>();
        foreach (var item in Pinned)
            if (WindowsFor(item) is { } w) pinnedWindows.UnionWith(w);
        var self = Environment.ProcessPath;
        var running = _running.Where(kv => RunningKeys.IsPath(kv.Key) && !RunningApps.IsShellHost(kv.Key)
                && !string.Equals(kv.Key, self, StringComparison.OrdinalIgnoreCase) && kv.Value.Any(h => !pinnedWindows.Contains(h)))
            .Select(kv => kv.Key).ToList();
        _unpinned.RemoveAll(i => !running.Contains(i.Target, StringComparer.OrdinalIgnoreCase));
        foreach (var exe in running)
            if (!_unpinned.Any(i => string.Equals(i.Target, exe, StringComparison.OrdinalIgnoreCase)))
                _unpinned.Add(new DockItem { Id = "netdock-running-" + exe.ToLowerInvariant(), Kind = ItemKind.App, Target = exe, Name = RunningApps.DisplayName(exe) });
        return !before.SequenceEqual(_unpinned.Select(i => i.Target));
    }

    /// <summary>Pin a running app: a real, saved item at the end of the pinned icons.</summary>
    public void PinRunning(DockItem item)
    {
        if (!IsUnpinned(item)) return;
        Pinned.Add(new DockItem { Kind = ItemKind.App, Name = item.Name, Target = _app.Tokens.Tokenize(item.Target)! });
        _app.Save();
        Relayout();
    }

    /// <summary>Ask every window of a running app to close.</summary>
    public void CloseWindowsOf(DockItem item)
    {
        if (WindowsFor(item) is { } wins) foreach (var w in wins) Win32.PostMessage(w, Win32.WM_CLOSE, 0, 0);
        Win32.SetTimer(Hwnd, RunningDebounce, 800, 0);
    }

    /// <summary>Track minimised top-level windows. True when the set changed.</summary>
    private bool UpdateMinimized()
    {
        var now = new List<nint>();
        if (Opt.MinimizeToDock)
            foreach (var wins in _running.Values)
                foreach (var h in wins)
                    if (Win32.IsIconic(h) && !now.Contains(h)) now.Add(h); // a window is listed under each of its keys
        var before = _minimized.Select(m => m.Hwnd).ToList();
        _minimized.RemoveAll(m => !now.Contains(m.Hwnd));
        foreach (var h in now)
        {
            if (_minimized.Any(m => m.Hwnd == h)) continue;
            var title = WindowTitle(h);
            var exe = RunningApps.PathOf(h);
            _minimized.Add((h, new DockItem
            {
                Id = "netdock-window-" + h, Kind = ItemKind.App, Target = SpecialItems.Window,
                Name = title.Length > 60 ? title[..57] + "..." : title,
                Icon = exe is null ? null : exe + ",0",
            }));
        }
        // Refresh titles of the ones we already had (a minimised player may change its title).
        for (var i = 0; i < _minimized.Count; i++) _minimized[i].Item.Name = WindowTitle(_minimized[i].Hwnd) is { Length: > 0 } t ? (t.Length > 60 ? t[..57] + "..." : t) : _minimized[i].Item.Name;
        return !before.SequenceEqual(_minimized.Select(m => m.Hwnd));
    }

    // ---------------------------------------------------------------- live previews

    private int _previewHover = -1;

    /// <summary>
    /// Hovering a running app opens its window previews after a short delay; once they're open, moving to
    /// another running app swaps them straight away, like the taskbar.
    /// </summary>
    private void OnHoverChanged()
    {
        _previewHover = _hover;
        Win32.KillTimer(Hwnd, PreviewTimer);
        if (!Opt.LivePreviews || _hover < 0 || _dragging >= 0 || _app.StackOpen) return;
        if (_app.Preview.Item is not null) { ShowPreview(_hover); return; }
        if (WindowsFor(Items[_hover]) is { Count: > 0 })
            Win32.SetTimer(Hwnd, PreviewTimer, (uint)Math.Max(1, Opt.PreviewDelayMs), 0);
    }

    private void ShowPreview(int index)
    {
        if (index < 0 || index >= Items.Count || _dragging >= 0 || _app.StackOpen || _userHidden) return;
        var item = Items[index];
        if (_app.Preview.Item == item) return;
        if (WindowsFor(item) is not { Count: > 0 } wins) { _app.Preview.Hide(); return; }
        // Anchor on the icon at full zoom so the popup clears the magnified icon.
        var r = IconScreenRect(index);
        var grow = (int)MathF.Ceiling(_max - _base);
        r = Opt.Edge switch
        {
            DockEdge.Top => new RECT(r.Left, r.Top, r.Right, r.Bottom + grow),
            DockEdge.Left => new RECT(r.Left, r.Top, r.Right + grow, r.Bottom),
            DockEdge.Right => new RECT(r.Left - grow, r.Top, r.Right, r.Bottom),
            _ => new RECT(r.Left, r.Top - grow, r.Right, r.Bottom),
        };
        _app.Preview.Show(item, wins, r, Opt.Edge, _scale);
        Render(); // drop the hover label
    }

    // ---------------------------------------------------------------- per-app volume

    private string? _volumeItem;   // item id whose volume was just changed (label shows the level)
    private float _volumeLevel;
    private bool _volumeMuted;
    private int _wheelRemainder;

    /// <summary>The audio sessions of an item's app (empty when it isn't playing or has no audio). Dispose it.</summary>
    public AppAudio.Sessions AudioFor(DockItem item) => AppAudio.For(RunningApps.KeysFor(item, _app.Tokens));

    /// <summary>Scroll over a running app's icon: its volume in 5 % steps, shown in the label for a moment.</summary>
    private void OnWheel(int delta, int screenX, int screenY)
    {
        if (!Opt.ScrollVolume) return;
        var (u, v) = ToEdge(screenX - _winX, screenY - _winY); // WM_MOUSEWHEEL has screen coordinates
        var hit = HitTest(u, v);
        if (hit < 0 || Items[hit].Kind == ItemKind.Separator) return;
        _wheelRemainder += delta;
        var notches = _wheelRemainder / 120; // WHEEL_DELTA; high-resolution wheels send smaller steps
        if (notches == 0) return;
        _wheelRemainder -= notches * 120;
        if (string.Equals(Items[hit].Target, SpecialItems.Volume, StringComparison.OrdinalIgnoreCase))
        {
            SystemStatus.SetMasterVolume(VolumeSteps.Step(SystemStatus.MasterVolume, notches));
            Render(); // the tile and its label show the new level
            return;
        }
        using var audio = AudioFor(Items[hit]);
        if (!audio.Any) { ShowVolume(Items[hit], -1, false); return; }
        if (audio.Muted && notches > 0) audio.Muted = false;
        var level = VolumeSteps.Step(audio.Volume, notches);
        audio.Volume = level;
        ShowVolume(Items[hit], level, audio.Muted);
    }

    public void ToggleMute(DockItem item)
    {
        using var audio = AudioFor(item);
        if (!audio.Any) return;
        audio.Muted = !audio.Muted;
        ShowVolume(item, audio.Volume, audio.Muted);
    }

    /// <summary>level &lt; 0 = the app has no audio playing.</summary>
    private void ShowVolume(DockItem item, float level, bool muted)
    {
        _volumeItem = item.Id;
        _volumeLevel = level;
        _volumeMuted = muted;
        Win32.SetTimer(Hwnd, VolumeTimer, 1500, 0);
        Render();
    }

    private bool VolumeShownFor(int i) => _volumeItem is not null && i >= 0 && i < Items.Count && Items[i].Id == _volumeItem;

    // ---------------------------------------------------------------- system corner tiles

    private readonly Dictionary<(string, int, bool), nint> _tileFormats = [];

    private nint TileFormat(string family, float size, bool bold = true)
    {
        var key = (family, (int)MathF.Round(size * 2), bold);
        if (!_tileFormats.TryGetValue(key, out var f))
            _tileFormats[key] = f = D2D.CreateTextFormat(family, MathF.Max(4, MathF.Round(size * 2) / 2), bold, centre: true, ellipsis: false);
        return f;
    }

    /// <summary>A live tile in place of an icon: rounded dark glass with the reading drawn on it.</summary>
    private void DrawTile(DockItem item, D2DRect r, float opacity)
    {
        var s = _surface;
        var size = r.R - r.L;
        var radius = size * 0.22f;
        s.FillRoundRect(r, radius, new D2DColor(0.10f, 0.11f, 0.13f, 0.86f * opacity));
        s.DrawRoundRect(r, radius, new D2DColor(1, 1, 1, 0.22f * opacity), MathF.Max(1, _scale));
        var white = new D2DColor(1, 1, 1, opacity);
        var dim = new D2DColor(0.75f, 0.78f, 0.82f, opacity);
        float cx = (r.L + r.R) / 2, cy = (r.T + r.B) / 2;
        switch (item.Target.ToLowerInvariant())
        {
            case SpecialItems.Clock:
            {
                var t = SystemStatus.Time;
                var timeSize = size * (t.Length > 5 ? 0.24f : 0.30f); // "7:42 PM" needs a smaller font than "19:42"
                s.DrawText(t, TileFormat("Segoe UI", timeSize), new D2DRect(r.L, r.T, r.R, cy + size * 0.10f), white);
                s.DrawText(SystemStatus.Date, TileFormat("Segoe UI", size * 0.17f, bold: false), new D2DRect(r.L, cy + size * 0.06f, r.R, r.B - size * 0.08f), dim);
                break;
            }
            case SpecialItems.Battery:
            {
                // Battery body with a fill, a nub on the right, and the percentage under it.
                float bw = size * 0.56f, bh = size * 0.28f, bx = cx - bw / 2 - size * 0.03f, by = cy - size * 0.22f;
                var body = new D2DRect(bx, by, bx + bw, by + bh);
                s.DrawRoundRect(body, bh * 0.2f, white, MathF.Max(1.2f, size * 0.035f));
                s.FillRect(new D2DRect(body.R + size * 0.015f, by + bh * 0.3f, body.R + size * 0.06f, by + bh * 0.7f), white);
                var pct = SystemStatus.HasBattery && SystemStatus.BatteryPercent >= 0 ? SystemStatus.BatteryPercent : 100;
                var inset = size * 0.05f;
                var fillColor = !SystemStatus.HasBattery ? dim : SystemStatus.Charging ? new D2DColor(0.35f, 0.85f, 0.45f, opacity)
                    : pct <= 20 ? new D2DColor(0.95f, 0.35f, 0.3f, opacity) : white;
                s.FillRect(new D2DRect(body.L + inset, body.T + inset, body.L + inset + (bw - inset * 2) * pct / 100f, body.B - inset), fillColor);
                var text = !SystemStatus.HasBattery ? L.T("AC") : (SystemStatus.Charging ? "⚡" : "") + pct + "%";
                s.DrawText(text, TileFormat("Segoe UI", size * 0.2f), new D2DRect(r.L, cy + size * 0.06f, r.R, r.B - size * 0.08f), white);
                break;
            }
            case SpecialItems.Network:
            {
                // Four rising bars; dimmed with a cross when there is no internet.
                var online = SystemStatus.Online;
                float barW = size * 0.09f, gap = size * 0.05f, baseY = cy + size * 0.22f, left = cx - (barW * 4 + gap * 3) / 2;
                for (var b = 0; b < 4; b++)
                {
                    var h = size * (0.14f + b * 0.11f);
                    var x0 = left + b * (barW + gap);
                    s.FillRect(new D2DRect(x0, baseY - h, x0 + barW, baseY), online ? white : new D2DColor(0.6f, 0.62f, 0.66f, 0.6f * opacity));
                }
                if (!online)
                {
                    var red = new D2DColor(0.95f, 0.35f, 0.3f, opacity);
                    float k = size * 0.12f, ox = cx + size * 0.2f, oy = cy - size * 0.18f;
                    s.DrawLine(new D2DPoint(ox - k, oy - k), new D2DPoint(ox + k, oy + k), red, MathF.Max(1.5f, size * 0.05f));
                    s.DrawLine(new D2DPoint(ox - k, oy + k), new D2DPoint(ox + k, oy - k), red, MathF.Max(1.5f, size * 0.05f));
                }
                break;
            }
            case SpecialItems.Volume:
            {
                // Segoe Fluent speaker glyph (Volume / Mute) with the level under it.
                var glyph = SystemStatus.MasterMuted ? "\uE74F" : "\uE767";
                s.DrawText(glyph, TileFormat("Segoe Fluent Icons", size * 0.36f, bold: false), new D2DRect(r.L, r.T + size * 0.06f, r.R, cy + size * 0.12f), white);
                var level = (int)MathF.Round(SystemStatus.MasterVolume * 100);
                s.DrawText(SystemStatus.MasterMuted ? L.T("Muted") : level + "%", TileFormat("Segoe UI", size * 0.2f),
                    new D2DRect(r.L, cy + size * 0.06f, r.R, r.B - size * 0.08f), SystemStatus.MasterMuted ? dim : white);
                break;
            }
        }
    }

    /// <summary>Re-read minimised windows now (after the option changes).</summary>
    public void RefreshMinimized() => RefreshRunning();

    private static string WindowTitle(nint h)
    {
        var buf = stackalloc char[256];
        var len = Win32.GetWindowText(h, buf, 256);
        return new string(buf, 0, Math.Max(0, len));
    }

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
        Win32.SetTimer(Hwnd, PopTimer, 80, 0);
        RecycleBinService.Start(Hwnd);
        RegisterToggleHotkey();
        RegisterSearchHotkey();
        Relayout();
        RefreshRunning();
        Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
        Render();
    }

    /// <summary>Recompute everything that depends on settings, monitor or DPI.</summary>
    public void Relayout()
    {
        var mon = Opt.Monitor == MonitorSpan.All ? Monitors.Span(Opt.Edge) : Monitors.Pick(Opt.Monitor);
        _work = mon.Work;
        _accent = Accent.For(Opt.AccentSync);
        if (Opt.AmbientGlow) Win32.SetTimer(Hwnd, AmbientTimer, 66, 0); else Win32.KillTimer(Hwnd, AmbientTimer);
        Win32.SetTimer(Hwnd, UpdateTimer, 3_600_000, 0); // hourly look at whether the daily update check is due
        _scale = mon.Dpi / 96f;
        _base = _baseFull = Opt.IconSize * _scale;
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
        _labelH = MathF.Round(Opt.LabelSize * 1.85f) * _scale;

        if (_labelFormat != 0) Com.Release(ref _labelFormat);
        try { _labelFormat = D2D.CreateTextFormat(Opt.LabelFont, Opt.LabelSize * _scale, bold: Opt.LabelBold, centre: true, ellipsis: false); }
        catch { _labelFormat = D2D.CreateTextFormat("Segoe UI", Opt.LabelSize * _scale, bold: Opt.LabelBold, centre: true, ellipsis: false); }
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
        Win32.SetWindowPos(Hwnd, InsertAfter, x, y, w, h, Win32.SWP_NOACTIVATE);
        foreach (var b in _bitmaps.Values) { var p = b; Com.Release(ref p); }
        _bitmaps.Clear();
        UpdateUnpinned();
        UpdateMinimized();
        SyncView();
        // System tiles refresh once a second (only while one is on the dock).
        if (Pinned.Any(SpecialItems.IsTile)) { SystemStatus.Refresh(); Win32.SetTimer(Hwnd, TileTimer, 1000, 0); }
        else Win32.KillTimer(Hwnd, TileTimer);
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
        Com.Release(ref _skinTinted);
        _skinPixels = null;
        _skin = null;
        if (!Panel || _app.Store.ResolveSkin(_app.Tokens.Expand(Opt.Skin)) is not { } folder) return;
        try
        {
            _skin = Skin.Load(folder, f => File.Exists(f) ? File.ReadAllText(f) : null);
            if (_skin is null) return;
            (_skinBg, _skinBgW, _skinBgH) = LoadImage(_skin.Background, out var bgPixels);
            _skinPixels = bgPixels;
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

    /// <summary>The skin background, or a copy blended 40 % toward the accent colour (rebuilt when the accent changes).</summary>
    private nint SkinBitmap()
    {
        if (_accent is not { } a || _skinPixels is not { } src) return _skinBg;
        if (_skinTinted != 0 && _skinTintFor is { } was && was.R == a.R && was.G == a.G && was.B == a.B) return _skinTinted;
        Com.Release(ref _skinTinted);
        const float K = 0.4f;
        var px = new byte[src.Length];
        for (var i = 0; i + 3 < src.Length; i += 4)
        {
            float alpha = src[i + 3];
            px[i] = (byte)(src[i] * (1 - K) + a.B * alpha * K);       // premultiplied: the accent scales with alpha
            px[i + 1] = (byte)(src[i + 1] * (1 - K) + a.G * alpha * K);
            px[i + 2] = (byte)(src[i + 2] * (1 - K) + a.R * alpha * K);
            px[i + 3] = src[i + 3];
        }
        _skinTinted = _surface.CreateBitmap(px, _skinBgW, _skinBgH);
        _skinTintFor = a;
        return _skinTinted != 0 ? _skinTinted : _skinBg;
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
    /// <summary>Background opacity (0..1) applied to the panel, skin, shelf and separators.</summary>
    private float BgA => Opt.BackgroundOpacity / 100f;
    private D2DColor Bg(D2DColor c) => new(c.R, c.G, c.B, c.A * BgA);

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
            _surface.DrawBitmap(bmp, new D2DRect(dx[xi], dy[yi], dx[xi + 1], dy[yi + 1]), BgA,
                new D2DRect(sx[xi], sy[yi], sx[xi + 1], sy[yi + 1]));
        }
    }

    /// <summary>The RocketDock look: a background that wraps the icons at rest size.</summary>
    private D2DRect DrawPanel(float rowStart, float rowEnd, float off)
    {
        var r = ToWindow(rowStart - _outStart, rowEnd + _outEnd, _shelfV0 - off, _iconV0 + _base + _outAway - off);
        if (_skin is { } k && _skinBg != 0)
        {
            var bg = SkinBitmap();
            // Skins are drawn for a bottom dock; like RocketDock, mirror them on the top edge
            // so the lit side still faces away from the screen edge.
            if (Opt.Edge == DockEdge.Top)
            {
                _surface.SetTransform(D2DMatrix.FlipY((r.T + r.B) / 2));
                NineSlice(bg, _skinBgW, _skinBgH, r, _skinL, k.Top, _skinR, k.Bottom);
                _surface.SetTransform(D2DMatrix.Identity);
            }
            else NineSlice(bg, _skinBgW, _skinBgH, r, _skinL, k.Top, _skinR, k.Bottom);
            return r;
        }
        // Built-in: dark smoked glass, lighter at the top, thin light rim (close to CrystalXP).
        var radius = 7 * _scale;
        _surface.FillRoundRect(r, radius, Bg(new D2DColor(0.09f, 0.1f, 0.12f, 0.82f)));
        if (_accent is { } accent) _surface.FillRoundRect(r, radius, Bg(accent.WithAlpha(0.38f)));
        LayeredSurface.SetGradientLine(_shelfBrush, new D2DPoint(r.L, r.T), new D2DPoint(r.L, r.B));
        LayeredSurface.SetBrushOpacity(_shelfBrush, BgA);
        _surface.FillRoundRect(r, radius, default, _shelfBrush, setColor: false);
        _surface.DrawRoundRect(r, radius, Bg(new D2DColor(1, 1, 1, 0.45f)), 1 * _scale);
        var inner = new D2DRect(r.L + 1.5f * _scale, r.T + 1.5f * _scale, r.R - 1.5f * _scale, r.B - 1.5f * _scale);
        _surface.DrawRoundRect(inner, radius - 1, Bg(new D2DColor(0, 0, 0, 0.35f)), 1 * _scale);
        return r;
    }

    /// <summary>Ambient glow: soft rings around the dock in the accent colour, breathing over four seconds.</summary>
    private void DrawGlow(D2DRect r)
    {
        var c = _accent ?? new D2DColor(0.45f, 0.75f, 1f, 1);
        var pulse = 0.6f + 0.4f * MathF.Sin(Environment.TickCount64 % 4000 / 4000f * MathF.Tau);
        const int Rings = 7;
        for (var k = Rings; k >= 1; k--)
        {
            var grow = k * 2.2f * _scale;
            var ring = new D2DRect(r.L - grow, r.T - grow, r.R + grow, r.B + grow);
            _surface.FillRoundRect(ring, 8 * _scale + grow, c.WithAlpha(0.07f * pulse * (1 - (k - 1f) / Rings)));
        }
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
        SyncView();
        var n = Items.Count;
        if (_u0.Length != n) { _u0 = new float[n]; _len = new float[n]; }
        if (_scales.Length != n) { _scales = new double[n]; Array.Fill(_scales, 1.0); }

        // Shrink to fit (RocketDock does too): if the icons at their set size don't fit along the edge,
        // make them smaller. Zoom still magnifies up to the set zoom size.
        _base = _baseFull;
        float natural = 0; var icons = 0;
        for (var i = 0; i < n; i++)
        {
            natural += RestLength(Items[i]) + Gap(i);
            if (Items[i].Kind != ItemKind.Separator) icons++;
        }
        var avail = _length - _pad * 4 - (Panel ? _outStart + _outEnd : _pad * 4);
        if (icons > 0 && natural > avail)
            _base = Math.Max(16 * _scale, (avail - (natural - icons * _baseFull)) / icons);

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

        if (Opt.AmbientGlow)
            DrawGlow(Panel ? ToWindow(rowStart - _outStart, rowEnd + _outEnd, _shelfV0 - off, _iconV0 + _base + _outAway - off)
                : ToWindow(rowStart - _pad * 2, rowEnd + _pad * 2, _shelfV0 - off, _shelfV0 + _shelfThick - off));
        if (Panel) DrawPanel(rowStart, rowEnd, off);
        else
        {
        // Shelf
        var shelfC = D2DColor.Parse(Opt.ShelfColor, 0x70182030);
        var shelf = ToWindow(rowStart - _pad * 2, rowEnd + _pad * 2, _shelfV0 - off, _shelfV0 + _shelfThick - off);
        var radius = 8 * _scale;
        s.FillRoundRect(shelf, radius, Bg(shelfC));
        if (_accent is { } accent) s.FillRoundRect(shelf, radius, Bg(accent.WithAlpha(0.38f)));
        var gradTop = Opt.Edge switch { DockEdge.Bottom => new D2DPoint(0, shelf.T), DockEdge.Top => new D2DPoint(0, shelf.B), DockEdge.Left => new D2DPoint(shelf.R, 0), _ => new D2DPoint(shelf.L, 0) };
        var gradBot = Opt.Edge switch { DockEdge.Bottom => new D2DPoint(0, shelf.B), DockEdge.Top => new D2DPoint(0, shelf.T), DockEdge.Left => new D2DPoint(shelf.L, 0), _ => new D2DPoint(shelf.R, 0) };
        LayeredSurface.SetGradientLine(_shelfBrush, gradTop, gradBot);
        LayeredSurface.SetBrushOpacity(_shelfBrush, BgA);
        s.FillRoundRect(shelf, radius, default, _shelfBrush, setColor: false);
        s.DrawRoundRect(shelf, radius, Bg(D2DColor.Parse(Opt.ShelfBorderColor, 0x90FFFFFF).WithAlpha(0.8f)), 1 * _scale);
        }

        var now = Environment.TickCount64;
        var indicator = D2DColor.Parse(Opt.IndicatorColor, 0xFFBFE4FF);
        if (_accent is { } ac) indicator = new D2DColor(ac.R + (1 - ac.R) * 0.55f, ac.G + (1 - ac.G) * 0.55f, ac.B + (1 - ac.B) * 0.55f, 1);
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
                        s.DrawBitmap(_skinSep, ToWindow(_u0[i], _u0[i] + _len[i], sv0 + k.SepBottom * _scale, sv1 - k.SepTop * _scale), BgA);
                    else
                        s.FillRect(ToWindow(mid - 0.5f * _scale, mid + 0.5f * _scale, sv0, sv1), Bg(new D2DColor(1, 1, 1, 0.3f)));
                    continue;
                }
                var a = ToWindow(mid - _scale, mid + _scale, _shelfV0 + _pad * 0.5f - off, _shelfV0 + _shelfThick - _pad * 0.5f - off);
                s.FillRect(a, new D2DColor(1, 1, 1, 0.35f));
                continue;
            }
            var fx = EffectFor(item.Id, now);
            var bounce = (float)(fx.Lift * _base);
            var v0 = _iconV0 + bounce - off;
            var fxLen = (float)(_len[i] * fx.Scale);
            var fxU0 = _u0[i] + (_len[i] - fxLen) / 2 + (float)(fx.Shift * _base);
            var rect = ToWindow(fxU0, fxU0 + fxLen, v0, v0 + fxLen);
            if (SpecialItems.IsTile(item))
            {
                var tileOpacity = Opt.IconOpacity / 100f;
                if (Opt.ZoomOpaque && _scales[i] > 1.02) tileOpacity += (1 - tileOpacity) * (float)Math.Min(1, (_scales[i] - 1) / 0.25);
                DrawTile(item, rect, tileOpacity * (float)fx.Alpha);
                continue;
            }
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
            opacity *= (float)fx.Alpha;
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
            var leaving = _dragUnlocked && _dragV > _thickness + _base * 0.25f;
            var r = ToWindow(_dragU - sz / 2, _dragU + sz / 2, _dragV - sz / 2, _dragV + sz / 2);
            s.DrawBitmap(db, r, leaving ? 0.35f : 0.9f);
        }

        // Label for the hovered icon.
        if ((Opt.ShowLabels || VolumeShownFor(_hover)) && _hover >= 0 && _hover < n && _dragging < 0 && _hide < 0.01 && Items[_hover].Kind != ItemKind.Separator && _app.Preview.Item is null)
            DrawLabel(_hover, off);

        s.EndAndPresent(Hwnd, _winX, _winY);
    }

    private void DrawLabel(int i, float off)
    {
        var volume = VolumeShownFor(i);
        var text = SpecialItems.IsTile(Items[i]) ? SystemStatus.LabelFor(Items[i].Target.ToLowerInvariant())
            : !volume ? Items[i].Name
            : _volumeLevel < 0 ? L.T("No sound playing")
            : _volumeMuted ? L.F("Muted ({0}%)", (int)MathF.Round(_volumeLevel * 100))
            : L.F("Volume {0}%", (int)MathF.Round(_volumeLevel * 100));
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
        var bg = D2DColor.Parse(Opt.LabelBackgroundColor, 0xC70D1219);
        if (bg.A > 0)
        {
            _surface.FillRoundRect(r, _labelH / 2, bg);
            _surface.DrawRoundRect(r, _labelH / 2, new D2DColor(1, 1, 1, 0.18f * bg.A), 1);
        }
        if (volume && _volumeLevel >= 0)
        {
            // Fill the pill up to the level, like a slider.
            var fill = new D2DRect(r.L, r.T, r.L + (r.R - r.L) * _volumeLevel, r.B);
            if (fill.R - fill.L > 1)
            {
                _surface.PushClip(new D2DRect(r.L + _labelH / 4, r.T, r.R - _labelH / 4, r.B));
                _surface.FillRect(fill, _volumeMuted ? new D2DColor(0.85f, 0.3f, 0.3f, 0.45f) : new D2DColor(0.35f, 0.75f, 1f, 0.45f));
                _surface.PopClip();
            }
        }
        var shadowC = D2DColor.Parse(Opt.LabelShadowColor, 0x99000000);
        if (shadowC.A > 0)
        {
            var d = MathF.Max(1, 1.5f * _scale);
            _surface.DrawText(text, _labelFormat, new D2DRect(r.L + d, r.T + d, r.R + d, r.B + d), shadowC);
        }
        var outline = D2DColor.Parse(Opt.LabelOutlineColor, 0);
        if (outline.A > 0)
        {
            var o = MathF.Max(1, _scale);
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        _surface.DrawText(text, _labelFormat, new D2DRect(r.L + dx * o, r.T + dy * o, r.R + dx * o, r.B + dy * o), outline);
        }
        _surface.DrawText(text, _labelFormat, r, D2DColor.Parse(Opt.LabelColor, 0xFFFFFFFF));
    }

    private EffectFrame EffectFor(string id, long now)
    {
        if (!_effects.TryGetValue(id, out var fx)) return EffectFrame.Rest;
        var t = (now - fx.Start) / 1000.0;
        if (t >= IconEffects.Duration)
        {
            if (_attention.ContainsKey(id)) { t %= IconEffects.Duration + 0.4; } // loop, with a short rest between runs
            else { _effects.Remove(id); return EffectFrame.Rest; }
        }
        return IconEffects.Sample(fx.Effect, t);
    }

    private void StartEffect(DockItem item, IconEffect effect)
    {
        if (effect == IconEffect.None) return;
        _effects[item.Id] = (Environment.TickCount64, effect);
        Kick();
    }

    /// <summary>HSHELL_FLASH: a window wants attention; animate the icon of its app until it is activated.</summary>
    private void OnFlash(nint hwnd)
    {
        if (Opt.AttentionEffect == IconEffect.None) return;
        var windowKeys = RunningApps.KeysOfWindow(hwnd);
        foreach (var item in Items)
        {
            if (RunningApps.KeysFor(item, _app.Tokens).FirstOrDefault(k => windowKeys.Contains(k, StringComparer.OrdinalIgnoreCase)) is not { } key) continue;
            if (_attention.ContainsKey(item.Id)) return;
            _attention[item.Id] = key;
            StartEffect(item, Opt.AttentionEffect);
            return;
        }
    }

    /// <summary>A window was activated: stop the attention loop of its app.</summary>
    private void OnActivated(nint hwnd)
    {
        if (_attention.Count == 0) return;
        if (hwnd == 0) return;
        var keys = RunningApps.KeysOfWindow(hwnd);
        foreach (var (id, key) in _attention.ToList())
            if (keys.Contains(key, StringComparer.OrdinalIgnoreCase)) _attention.Remove(id);
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
        // Time-based so AutoHideDurationMs holds whatever the timer resolution is.
        var now = Environment.TickCount64;
        var dt = _lastTick == 0 ? 10 : Math.Clamp(now - _lastTick, 1, 100);
        _lastTick = now;
        var hideTarget = _hideWanted ? 1.0 : 0.0;
        if (_hideP != hideTarget)
        {
            var step = Opt.AutoHideDurationMs <= 0 ? 1.0 : dt / (double)Opt.AutoHideDurationMs;
            _hideP = hideTarget > _hideP ? Math.Min(hideTarget, _hideP + step) : Math.Max(hideTarget, _hideP - step);
            moving = _hideP != hideTarget || moving;
        }
        _hide = _hideP * _hideP * (3 - 2 * _hideP); // smoothstep
        if (_effects.Count > 0 || _dragging >= 0) moving = true;

        if (_cursorU is { } cu) _hover = HitTest(cu, _cursorV);
        if (_hover != _previewHover) OnHoverChanged();
        Render();
        if (!moving)
        {
            _animating = false;
            _lastTick = 0;
            Win32.KillTimer(Hwnd, AnimTimer);
        }
    }

    // ---------------------------------------------------------------- running apps

    private void RefreshRunning()
    {
        _running = RunningApps.Snapshot();
        // An app that closed while flashing stops asking for attention.
        foreach (var (id, exe) in _attention.ToList())
            if (!_running.ContainsKey(exe)) _attention.Remove(id);
        UpdateUnpinned();
        UpdateMinimized();
        Render();
    }

    private bool IsRunning(DockItem item) => WindowsFor(item) is { Count: > 0 };

    private List<nint>? WindowsFor(DockItem item) =>
        WindowsByKeys(RunningApps.KeysFor(item, _app.Tokens));

    /// <summary>Windows matching any of the keys, without duplicates, in first-seen order.</summary>
    private List<nint>? WindowsByKeys(List<string> keys)
    {
        List<nint>? found = null;
        foreach (var k in keys)
            if (_running.TryGetValue(k, out var w))
                foreach (var h in w)
                    if (!(found ??= []).Contains(h)) found.Add(h);
        return found;
    }

    // ---------------------------------------------------------------- input

    private void Activate(int index, bool newInstance, bool elevate)
    {
        var item = Items[index];
        switch (item.Kind)
        {
            case ItemKind.Separator:
                return;
            case ItemKind.App when item.Target == SpecialItems.Window:
                if (_minimized.FirstOrDefault(m => m.Item == item) is { Hwnd: not 0 } win) RunningApps.Activate(win.Hwnd);
                Win32.SetTimer(Hwnd, RunningDebounce, 300, 0);
                return;
            case ItemKind.App when SpecialItems.IsTile(item):
                StartEffect(item, Opt.ClickEffect);
                switch (item.Target.ToLowerInvariant())
                {
                    case SpecialItems.Clock: Launcher.Open("ms-clock:"); break;
                    case SpecialItems.Battery: Launcher.Open("ms-settings:batterysaver"); break;
                    case SpecialItems.Network: Launcher.Open("ms-settings:network-status"); break;
                    case SpecialItems.Volume: SystemStatus.ToggleMasterMute(); Render(); break;
                }
                return;
            case ItemKind.App when SpecialItems.Is(item):
                StartEffect(item, Opt.ClickEffect);
                if (string.Equals(item.Target, SpecialItems.Settings, StringComparison.OrdinalIgnoreCase)) SettingsWindow.Show(_app);
                else if (string.Equals(item.Target, SpecialItems.Quit, StringComparison.OrdinalIgnoreCase)) Close();
                else if (string.Equals(item.Target, SpecialItems.Search, StringComparison.OrdinalIgnoreCase)) _app.Search.Show();
                return;
            case ItemKind.Stack:
                if (item.Stack?.View == StackView.Open) Launcher.Open(_app.Tokens.Expand(item.Target)!);
                else { _app.Preview.Hide(); _app.ToggleStack(item, IconScreenRect(index)); }
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
        StartEffect(item, Opt.ClickEffect);
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

        if (_down >= 0 && _down < Pinned.Count && _dragging < 0 && (!Opt.Locked || Win32.GetKeyState(0x11) < 0)
            && (Math.Abs(x - _downPt.X) > 6 * _scale || Math.Abs(y - _downPt.Y) > 6 * _scale))
        {
            _dragging = _down;
            _dragUnlocked = true;
        }
        if (_dragging >= 0)
        {
            _dragU = u; _dragV = v;
            // Live reorder: move the dragged item to the slot under the cursor.
            var target = InsertIndex(u);
            if (target != _dragging && target >= 0 && target < Pinned.Count)
            {
                var it = Pinned[_dragging];
                Pinned.RemoveAt(_dragging);
                Pinned.Insert(target, it);
                SyncView();
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
            var leaving = _dragUnlocked && (v > _thickness + _base * 0.25f || v < -_base);
            if (leaving) { Pinned.RemoveAt(_dragging); SyncView(); }
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
        var paths = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            // Ask for the length first: a fixed buffer would silently truncate long paths, and a truncated
            // path handed to the Recycle Bin or a copy could name a different file or folder.
            var len = Win32.DragQueryFile(hdrop, i, null, 0);
            if (len == 0) continue;
            var buf = new char[len + 1];
            fixed (char* b = buf)
                if (Win32.DragQueryFile(hdrop, i, b, len + 1) == len) paths.Add(new string(b, 0, (int)len));
        }
        Win32.DragFinish(hdrop);
        if (paths.Count == 0) return;

        // The centre of an icon acts on it; its edges and the gaps add new items (see DropRules).
        var hit = HitTest(u, v);
        var target = hit >= 0 ? Items[hit] : null;
        var across = hit >= 0 && _len[hit] > 0 ? (u - _u0[hit]) / _len[hit] : 0.5f;
        switch (DropRules.Decide(target, across))
        {
            case DropAction.Recycle:
                RecycleBinService.Recycle(Hwnd, paths);
                Bounce(target!);
                return;
            case DropAction.CopyInto:
                if (_app.Tokens.Expand(target!.Target) is { } folder && Directory.Exists(folder))
                {
                    ShellFiles.CopyInto(Hwnd, paths, folder);
                    Bounce(target);
                    return;
                }
                break;
            case DropAction.OpenWith:
                Launcher.Launch(target!, _app.Tokens, arguments: DropRules.OpenWithArguments(target!.Arguments, paths));
                Bounce(target);
                return;
        }

        int at;
        if (hit >= 0) at = across < 0.5f ? hit : hit + 1;
        else at = Items.Count == 0 ? 0 : Math.Clamp(InsertIndex(u) + (u > _u0[^1] + _len[^1] / 2 ? 1 : 0), 0, Items.Count);
        at = Math.Min(at, Pinned.Count); // never among the minimised-window icons
        foreach (var path in paths)
            if (ItemFactory.FromPath(path, _app.Tokens) is { } item) Pinned.Insert(Math.Min(at++, Pinned.Count), item);
        _app.Save();
        Relayout();
    }

    private void Bounce(DockItem item)
    {
        StartEffect(item, Opt.ClickEffect);
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
        // Minimised-window icons aren't saved items; treat them like the empty end of the dock.
        if (hit >= Pinned.Count)
        {
            if (Items[hit] is var running && IsUnpinned(running)) { Menus.ShowRunningMenu(_app, this, running, p); return; }
            hit = -1;
        }
        Menus.ShowDockMenu(_app, this, hit >= 0 ? Items[hit] : null, hit, p);
    }

    public void InsertItem(int at, DockItem item)
    {
        Pinned.Insert(Math.Clamp(at, 0, Pinned.Count), item);
        _app.Save();
        Relayout();
    }

    protected override nint WndProc(uint msg, nint w, nint l)
    {
        if (msg == _shellHookMsg && _shellHookMsg != 0)
        {
            // HSHELL_FLASH (0x8006) asks for attention; HSHELL_WINDOWACTIVATED / RUDEAPPACTIVATED (4 / 0x8004) end it.
            switch ((int)w)
            {
                case 0x8006: OnFlash(l); return 0;
                case 4 or 0x8004: OnActivated(l); break;
            }
            // HSHELL_WINDOWCREATED / DESTROYED / ACTIVATED all mean the running set may have changed.
            Win32.SetTimer(Hwnd, RunningDebounce, 150, 0);
            return 0;
        }
        switch (msg)
        {
            case Win32.WM_MOUSEACTIVATE:
                // Normal layering: a click brings the dock in front of other windows (without taking focus).
                if (Opt.Layer == DockLayer.Normal)
                    Win32.SetWindowPos(Hwnd, 0 /* HWND_TOP */, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                return Win32.MA_NOACTIVATE;
            case 0x46: // WM_WINDOWPOSCHANGING: keep an always-on-bottom dock under everything
                if (Opt.Layer == DockLayer.AlwaysOnBottom && !_popped)
                {
                    var wp = (WINDOWPOS*)l;
                    if ((wp->flags & Win32.SWP_NOZORDER) == 0) wp->hwndInsertAfter = 1; // HWND_BOTTOM
                }
                break;
            case Win32.WM_MOUSEMOVE:
                OnMouseMove(Win32.LoWord(l), Win32.HiWord(l));
                return 0;
            case Win32.WM_MOUSELEAVE:
                OnMouseLeave();
                return 0;
            case Win32.WM_LBUTTONDOWN:
                _app.Preview.Hide();
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
            case Win32.WM_MOUSEWHEEL:
                OnWheel((short)((w >> 16) & 0xFFFF), Win32.LoWord(l), Win32.HiWord(l));
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
                _app.Preview.Hide();
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
                    case PopTimer: PollPopUp(); break;
                    case UpdateTimer:
                        _app.MaybeCheckForUpdates();
                        break;
                    case AmbientTimer:
                        if (_hide < 0.99) Render();
                        break;
                    case TileTimer:
                        if (SystemStatus.Refresh()) Render();
                        break;
                    case VolumeTimer:
                        Win32.KillTimer(Hwnd, VolumeTimer);
                        _volumeItem = null;
                        Render();
                        break;
                    case PreviewTimer:
                        Win32.KillTimer(Hwnd, PreviewTimer);
                        ShowPreview(_hover);
                        break;
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
            case 0x312: // WM_HOTKEY
                if (w == HotkeyId) ToggleVisible();
                else if (w == SearchHotkeyId) _app.Search.Toggle();
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
                else if (w == 0x14 /* SPI_SETDESKWALLPAPER */ && Opt.AccentSync != AccentSource.None) { Accent.Invalidate(); _accent = Accent.For(Opt.AccentSync); Render(); }
                return 0;
            case 0x320: // WM_DWMCOLORIZATIONCOLORCHANGED: the Windows accent changed
                if (Opt.AccentSync != AccentSource.None) { _accent = Accent.For(Opt.AccentSync); Render(); }
                return 0;
            case Win32.WM_DESTROY:
                _app.Icons.RemoveListener(Hwnd);
                Win32.PostQuitMessage(0);
                return 0;
        }
        if (msg == App.WM_TRAY) { _app.OnTray(this, (uint)l); return 0; }
        if (msg == App.WM_TASKBARCREATED && msg != 0) { _app.OnTaskbarCreated(); return 0; }
        if (msg == Updater.WM_UPDATE_APPLIED) { _app.OnUpdateApplied(w == 1); return 0; }
        if (msg == App.WM_COMMAND_LINE) { _app.RunCommandLine(w); return 0; }
        if (msg == Updater.WM_UPDATE_FOUND) { _app.OnUpdateChecked((int)w, l == 1); return 0; }
        return Default(msg, w, l);
    }

    public void Close() => Win32.DestroyWindow(Hwnd);

    /// <summary>Z-order slot for <see cref="DockOptions.Layer"/>.</summary>
    private nint InsertAfter => _popped ? Win32.HWND_TOPMOST : Opt.Layer switch
    {
        DockLayer.Normal => -2,         // HWND_NOTOPMOST
        DockLayer.AlwaysOnBottom => 1,  // HWND_BOTTOM
        _ => Win32.HWND_TOPMOST,
    };

    // ---------------------------------------------------------------- pop up on mouse-over

    private bool _popped;
    private long _edgeSince;

    /// <summary>
    /// A dock that isn't always on top comes over other windows when the cursor rests on the screen edge
    /// in front of it (RocketDock: "Pop up on mouse-over"), and goes back once the cursor leaves it.
    /// Polled, because a covered dock gets no mouse messages.
    /// </summary>
    private void PollPopUp()
    {
        if (_userHidden || Items.Count == 0) return;
        POINT pt;
        Win32.GetCursorPos(&pt);
        var (u, v) = ToEdge(pt.X - _winX, pt.Y - _winY);
        var alongDock = u >= _u0[0] - 8 * _scale && u <= _u0[^1] + _len[^1] + 8 * _scale;

        if (!_popped)
        {
            if (!Opt.PopUp || Opt.Layer == DockLayer.AlwaysOnTop || !alongDock || v < -1 || v > 2 * _scale) { _edgeSince = 0; return; }
            var now = Environment.TickCount64;
            if (_edgeSince == 0) _edgeSince = now;
            if (now - _edgeSince < Opt.PopUpDelayMs) return;
            _popped = true;
            Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            return;
        }

        // Stay up while the cursor is over the dock's band, a drag is running or a stack is open.
        var over = alongDock && v >= -1 && v <= _thickness;
        if (over || _dragging >= 0 || _app.StackOpen) return;
        _popped = false;
        _edgeSince = 0;
        Win32.SetWindowPos(Hwnd, InsertAfter, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPOS { public nint hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    // ---------------------------------------------------------------- show/hide hotkey

    private const int HotkeyId = 1, SearchHotkeyId = 2;
    private bool _userHidden;
    public bool IsUserHidden => _userHidden;

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint h, int id);

    /// <summary>(Re)register the hotkey from settings. False if another program already owns it.</summary>
    public bool RegisterSearchHotkey()
    {
        UnregisterHotKey(Hwnd, SearchHotkeyId);
        if (Hotkey.Parse(Opt.SearchHotkey) is not { } k) return true;
        return RegisterHotKey(Hwnd, SearchHotkeyId, k.Modifiers | 0x4000 /* MOD_NOREPEAT */, k.VirtualKey);
    }

    /// <summary>Click a dock item by id, as the search overlay does for "On the dock" results.</summary>
    public void ActivateById(string id, bool elevate)
    {
        var index = Items.FindIndex(i => i.Id == id);
        if (index >= 0) Activate(index, newInstance: false, elevate);
    }

    public bool RegisterToggleHotkey()
    {
        UnregisterHotKey(Hwnd, HotkeyId);
        if (Hotkey.Parse(Opt.ToggleHotkey) is not { } k) return true;
        return RegisterHotKey(Hwnd, HotkeyId, k.Modifiers | 0x4000 /* MOD_NOREPEAT */, k.VirtualKey);
    }

    /// <summary>Hide the dock completely (no drawing, no hit area) or bring it back.</summary>
    public void ToggleVisible()
    {
        _userHidden = !_userHidden;
        if (_userHidden)
        {
            _app.Stack.Close();
            _app.Preview.Hide();
            Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
        }
        else
        {
            Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
            Render();
        }
    }
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

    /// <summary>Every monitor as one area for a spanning dock; DPI from the primary monitor.</summary>
    public static MonitorInfo Span(DockEdge edge)
    {
        var infos = All().Select(Info).ToList();
        var primary = Pick(0);
        if (infos.Count < 2) return primary;
        var (l, t, r, b) = MonitorSpan.Union([.. infos.Select(i => (i.Work.Left, i.Work.Top, i.Work.Right, i.Work.Bottom))], edge);
        var (bl, bt, br, bb) = MonitorSpan.Union([.. infos.Select(i => (i.Bounds.Left, i.Bounds.Top, i.Bounds.Right, i.Bounds.Bottom))], edge);
        return new MonitorInfo(new RECT(bl, bt, br, bb), new RECT(l, t, r, b), primary.Dpi);
    }

    public static MonitorInfo ForPoint(int x, int y) => Info(Win32.MonitorFromPoint(new POINT(x, y), 2 /* NEAREST */));
}
