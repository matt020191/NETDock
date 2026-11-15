using System.Reflection;
using System.Text.Json;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// Dock Settings: RocketDock's tabbed settings, modernised. Every change previews live on the
/// dock; OK saves, Cancel (or closing) puts the previous settings back.
/// </summary>
public sealed unsafe class SettingsWindow : WindowBase
{
    public static SettingsWindow? Current { get; private set; }

    private const int IdNav = 100, IdOk = 1, IdCancel = 2, IdDefaults = 10;
    private static readonly (string Glyph, string Name)[] Pages =
    [
        ("", "General"), ("", "Icons"), ("", "Position"),
        ("", "Style"), ("", "Behavior"), ("", "About"),
    ];

    private readonly App _app;
    private Layout _ui = null!;
    private nint _font, _titleFont, _glyphFont, _nav, _title;
    private string _snapshot = "";
    private bool _startWithWindows;
    private int _nextId = 200;
    private uint _accent;
    private nint _whiteBrush;

    private readonly Dictionary<int, Action<int>> _onCommand = [];   // id → handler(notification code)
    private readonly Dictionary<nint, Action<int>> _onSlide = [];    // trackbar → handler(pos)
    private readonly List<Action> _refresh = [];                    // re-read controls after Defaults

    private SettingsWindow(App app) => _app = app;
    private DockOptions Opt => _app.Settings.Dock;

    public static void Show(App app)
    {
        if (Current is { } c) { Win32.ShowWindow(c.Hwnd, Win32.SW_RESTORE); Win32.SetForegroundWindow(c.Hwnd); return; }
        Current = new SettingsWindow(app);
        Current.Open();
    }

    private void Open()
    {
        _snapshot = JsonSerializer.Serialize(Opt, SettingsJson.Default.DockOptions);
        _startWithWindows = App.StartsWithWindows;
        _accent = Ui.AccentColor();
        _whiteBrush = Ui.GetSysColorBrush(5);

        POINT p;
        Win32.GetCursorPos(&p);
        var mon = Monitors.ForPoint(p.X, p.Y);
        var scale = mon.Dpi / 96f;
        int w = (int)(684 * scale), h = (int)(644 * scale);
        Create("NETDock.Settings", Win32.WS_EX_DLGMODALFRAME, Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_SYSMENU | 0x20000 /* MINIMIZEBOX */,
            mon.Work.Left + (mon.Work.Width - w) / 2, mon.Work.Top + (mon.Work.Height - h) / 2, w, h, "Dock Settings", sysColor: 5);

        _font = Ui.CreateFont(-(int)(12 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _titleFont = Ui.CreateFont(-(int)(20 * scale), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _glyphFont = Ui.CreateFont(-(int)(20 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe Fluent Icons");
        _ui = new Layout(Hwnd, scale, _font);

        _nav = _ui.Add("LISTBOX", null, Ui.LBS_NOTIFY | Ui.LBS_OWNERDRAWFIXED | Ui.LBS_HASSTRINGS | Ui.LBS_NOINTEGRALHEIGHT | Win32.WS_TABSTOP,
            12, 12, 132, 520, IdNav);
        foreach (var (_, name) in Pages) fixed (char* s = name) Win32.SendMessage(_nav, Ui.LB_ADDSTRING, 0, (nint)s);
        _title = _ui.Label("", 168, 12, 480, 34);
        Win32.SendMessage(_title, Win32.WM_SETFONT, _titleFont, 1);

        BuildGeneral(); BuildIcons(); BuildPosition(); BuildStyle(); BuildBehavior(); BuildAbout();
        _ui.Page = -1;
        _ui.Button("Defaults", 12, 552, 110, 30, IdDefaults);
        _ui.Button("OK", 448, 552, 100, 30, IdOk, isDefault: true);
        _ui.Button("Cancel", 556, 552, 100, 30, IdCancel);

        SelectPage(0);
        Win32.ShowWindow(Hwnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(Hwnd);
    }

    private void SelectPage(int i)
    {
        Win32.SendMessage(_nav, Ui.LB_SETCURSEL, i, 0);
        Ui.SetWindowText(_title, Pages[i].Name);
        _ui.ShowPage(i);
    }

    // ------------------------------------------------------------ building blocks

    private const float X = 172, Wide = 470;

    private int Id() => _nextId++;

    private void Apply(Action change)
    {
        change();
        _app.Settings.Sanitize();
        _app.Dock.Relayout();
    }

    /// <summary>Title, then "min ──●── max   (value)" like RocketDock.</summary>
    private void Slider(ref float y, string title, int min, int max, Func<int> get, Action<int> set, Func<int, string> fmt)
    {
        _ui.Label(title, X, y, Wide);
        _ui.Label(fmt(min), X, y + 26, 64, 20, Ui.SS_RIGHT);
        var id = Id();
        var bar = _ui.Slider(X + 70, y + 20, 270, id, min, max, get());
        _ui.Label(fmt(max), X + 346, y + 26, 64);
        var value = _ui.Label($"({fmt(get())})", X + 410, y + 26, 70);
        _onSlide[bar] = pos => { Apply(() => set(pos)); Ui.SetWindowText(value, $"({fmt(get())})"); };
        _refresh.Add(() => { Win32.SendMessage(bar, Ui.TBM_SETPOS, 1, get()); Ui.SetWindowText(value, $"({fmt(get())})"); });
        y += 56;
    }

    private void Check(ref float y, string text, Func<bool> get, Action<bool> set)
    {
        var id = Id();
        var c = _ui.Check(text, X, y, Wide, id, get());
        _onCommand[id] = code => { if (code == Ui.BN_CLICKED) Apply(() => set(Layout.Checked(c))); };
        _refresh.Add(() => Win32.SendMessage(c, Ui.BM_SETCHECK, get() ? 1 : 0, 0));
        y += 30;
    }

    private void Combo(ref float y, string title, string[] items, Func<int> get, Action<int> set)
    {
        _ui.Label(title, X, y, Wide);
        var id = Id();
        var c = _ui.Combo(X + 70, y + 22, 270, id, items, get());
        _onCommand[id] = code => { if (code == Ui.CBN_SELCHANGE) Apply(() => set(Layout.Get(c, Ui.CB_GETCURSEL))); };
        _refresh.Add(() => Win32.SendMessage(c, Ui.CB_SETCURSEL, get(), 0));
        y += 58;
    }

    private void Btn(float x, float y, float w, string text, System.Action run)
    {
        var id = Id();
        _ui.Button(text, x, y, w, 30, id);
        _onCommand[id] = code => { if (code == Ui.BN_CLICKED) run(); };
    }

    // ------------------------------------------------------------ pages

    private void BuildGeneral()
    {
        _ui.Page = 0;
        float y = 60;
        var id = Id();
        var startup = _ui.Check("Start NETDock with Windows", X, y, Wide, id, _startWithWindows);
        _onCommand[id] = code => { if (code == Ui.BN_CLICKED) _startWithWindows = Layout.Checked(startup); };
        y += 30;
        Check(ref y, "Lock icons (no dragging to reorder or remove)", () => Opt.Locked, v => Opt.Locked = v);

        y += 16;
        _ui.Group("Settings storage", X - 6, y, Wide + 6, 200);
        var loc = _app.Store.Location;
        _ui.Label(loc.IsPortable ? "Portable: settings and icons are kept next to NETDock.exe" : "Settings are kept in your user profile",
            X + 8, y + 26, Wide - 24);
        var path = _ui.Edit(loc.Folder, X + 8, y + 50, Wide - 24, Id(), Ui.ES_READONLY);
        _ = path;
        Btn(X + 8, y + 88, 140, "Open folder", () => { Directory.CreateDirectory(loc.Folder); Launcher.Open(loc.Folder); });
        Btn(X + 156, y + 88, 140, "Export backup...", () => Menus.Run(_app, 91));
        Btn(X + 304, y + 88, 140, "Import backup...", () => { Menus.Run(_app, 92); RefreshAll(); });
        Btn(X + 8, y + 126, 140, "Import RocketDock...", () => { Menus.Run(_app, 90); RefreshAll(); });
        if (!loc.IsPortable) Btn(X + 156, y + 126, 140, "Make portable...", () => Menus.Run(_app, 94));
    }

    private void BuildIcons()
    {
        _ui.Page = 1;
        float y = 56;
        Slider(ref y, "Opacity", 10, 100, () => Opt.IconOpacity, v => Opt.IconOpacity = v, v => $"{v}%");
        Check(ref y, "Zoomed icons are fully opaque", () => Opt.ZoomOpaque, v => Opt.ZoomOpaque = v);
        Slider(ref y, "Size", 16, 128, () => Opt.IconSize, v => { Opt.IconSize = v; Opt.MagnifiedSize = Math.Max(Opt.MagnifiedSize, v); }, v => $"{v}px");
        Combo(ref y, "Hover effect", ["None", "Zoom"], () => Opt.Magnify ? 1 : 0, v => Opt.Magnify = v == 1);
        Slider(ref y, "Zoom", 16, 256, () => Opt.MagnifiedSize, v => Opt.MagnifiedSize = Math.Max(v, Opt.IconSize), v => $"{v}px");
        Slider(ref y, "Zoom width", 2, 16, () => (int)Math.Round(Opt.MagnifyRange * 2), v => Opt.MagnifyRange = v / 2.0, v => $"{v}");
        Slider(ref y, "Zoom duration", 30, 600, () => Opt.ZoomDurationMs, v => Opt.ZoomDurationMs = v, v => $"{v}ms");
        Slider(ref y, "Icon spacing", 0, 32, () => Opt.Spacing, v => Opt.Spacing = v, v => $"{v}px");
        Slider(ref y, "Separator spacing", 0, 48, () => Opt.SeparatorSpacing, v => Opt.SeparatorSpacing = v, v => $"{v}px");
    }

    private void BuildPosition()
    {
        _ui.Page = 2;
        float y = 60;
        var monitors = Monitors.All();
        var screens = new List<string> { "Primary monitor" };
        for (var i = 0; i < monitors.Count; i++)
        {
            var b = Monitors.Info(monitors[i]).Bounds;
            screens.Add($"Monitor {i + 1}  ({b.Width} x {b.Height})");
        }
        Combo(ref y, "Screen", [.. screens], () => Math.Min(Opt.Monitor, screens.Count - 1), v => Opt.Monitor = v);
        Combo(ref y, "Screen edge", ["Bottom", "Top", "Left", "Right"], () => (int)Opt.Edge, v => Opt.Edge = (DockEdge)v);
        Slider(ref y, "Centering", 0, 100, () => (int)Math.Round(Opt.Centre * 100), v => Opt.Centre = v / 100.0, v => $"{v}%");
        Slider(ref y, "Edge offset", 0, 100, () => Opt.EdgeOffset, v => Opt.EdgeOffset = v, v => $"{v}px");
        Combo(ref y, "Layering", ["Always on top", "Normal (in front when clicked)", "Always on bottom"], () => (int)Opt.Layer, v => Opt.Layer = (DockLayer)v);
        Check(ref y, "Pop up on mouse-over (when not always on top)", () => Opt.PopUp, v => Opt.PopUp = v);
        Slider(ref y, "Pop-up delay", 0, 2000, () => Opt.PopUpDelayMs, v => Opt.PopUpDelayMs = v, v => $"{v}ms");
    }

    private List<(string Name, string Reference, string? Source)> _skins = [];

    private void BuildStyle()
    {
        _ui.Page = 3;
        float y = 60;
        Combo(ref y, "Style", ["Background panel (RocketDock)", "Platform shelf (macOS)"], () => (int)Opt.Style, v => Opt.Style = (DockStyle)v);
        _skins = Menus.SkinChoices(_app);
        var names = new List<string> { "Built-in panel" };
        names.AddRange(_skins.Select(s => s.Name));
        Combo(ref y, "Skin (background panel)", [.. names],
            () => Opt.Skin is null ? 0 : Math.Max(0, _skins.FindIndex(s => s.Reference == Opt.Skin) + 1),
            v =>
            {
                if (v == 0) { Opt.Skin = null; return; }
                var choice = _skins[v - 1];
                Opt.Skin = choice.Source is { } src ? _app.Store.ImportSkin(src) : choice.Reference;
            });
        Slider(ref y, "Panel padding (above and below icons)", 0, 16, () => Opt.PanelPadding, v => Opt.PanelPadding = v, v => $"{v}px");
        Check(ref y, "Show labels on hover", () => Opt.ShowLabels, v => Opt.ShowLabels = v);
        Check(ref y, "Show running indicators", () => Opt.ShowRunningIndicators, v => Opt.ShowRunningIndicators = v);
        Check(ref y, "Reflections (platform shelf)", () => Opt.Reflections, v => Opt.Reflections = v);
    }

    private void BuildBehavior()
    {
        _ui.Page = 4;
        float y = 60;
        Check(ref y, "Auto-hide", () => Opt.AutoHide, v => Opt.AutoHide = v);
        Slider(ref y, "Auto-hide delay", 0, 3000, () => Opt.AutoHideDelayMs, v => Opt.AutoHideDelayMs = v, v => $"{v}ms");
        Combo(ref y, "Clicking a program that is already running", ["Switches to it (click again to minimise)", "Starts another copy"],
            () => Opt.ActivateIfRunning ? 0 : 1, v => Opt.ActivateIfRunning = v == 0);
        _ui.Label("Shift+click or middle-click always starts another copy; Ctrl+Shift+click runs as administrator.", X, y, Wide, 40);
        y += 50;
        _ui.Label("Show/hide hotkey", X, y, Wide);
        var id = Id();
        var edit = _ui.Edit(Opt.ToggleHotkey ?? "", X + 70, y + 22, 180, id);
        var status = _ui.Label("", X + 260, y + 25, 220);
        void Validate()
        {
            var text = Layout.Text(edit);
            Opt.ToggleHotkey = text.Length == 0 ? null : text;
            var ok = Hotkey.Parse(text) is not null || text.Length == 0;
            var registered = ok && _app.Dock.RegisterToggleHotkey();
            Ui.SetWindowText(status, text.Length == 0 ? "No hotkey" : !ok ? "Not a valid key, e.g. Ctrl+Alt+R" : registered ? "\u2713 Active" : "In use by another program");
        }
        _onCommand[id] = code => { if (code == Ui.EN_CHANGE) Validate(); };
        _refresh.Add(() => { Ui.SetWindowText(edit, Opt.ToggleHotkey ?? ""); Validate(); });
        Validate();
        _ui.Label("Hides the whole dock until pressed again. Ctrl-drag moves or removes icons even when they are locked.", X, y + 56, Wide, 40);
    }

    private void BuildAbout()
    {
        _ui.Page = 5;
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
        float y = 60;
        _ui.Label($"NETDock {version}", X, y, Wide, 24); y += 28;
        _ui.Label("A RocketDock-style dock for Windows. Native .NET 11 AOT, drawn with Direct2D.", X, y, Wide, 20); y += 40;
        _ui.Label("Inspired by RocketDock (Punk Software) and Aero Dock (MIT, Ryan Panda).", X, y, Wide, 20); y += 24;
        _ui.Label("RocketDock skins are read in their original format.", X, y, Wide, 20); y += 40;
        _ui.Label("Settings: " + _app.Store.Location.SettingsFile, X, y, Wide, 40);
    }

    private void RefreshAll() { foreach (var r in _refresh) r(); }

    // ------------------------------------------------------------ closing

    private void Finish(bool ok)
    {
        if (ok)
        {
            if (_startWithWindows != App.StartsWithWindows) App.StartsWithWindows = _startWithWindows;
            _app.Save();
        }
        else
        {
            _app.Settings.Dock = JsonSerializer.Deserialize(_snapshot, SettingsJson.Default.DockOptions)!;
            _app.Settings.Sanitize();
            _app.Dock.Relayout();
            _app.Dock.RegisterToggleHotkey();
        }
        Win32.DestroyWindow(Hwnd);
    }

    protected override nint WndProc(uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case Win32.WM_COMMAND:
            {
                var id = (int)(w & 0xFFFF);
                var code = (int)((w >> 16) & 0xFFFF);
                switch (id)
                {
                    case IdOk: Finish(true); return 0;
                    case IdCancel: Finish(false); return 0;
                    case IdDefaults:
                        Apply(() =>
                        {
                            var keep = (Opt.Skin, Opt.Style);
                            _app.Settings.Dock = new DockOptions { Skin = keep.Skin, Style = keep.Style };
                        });
                        RefreshAll();
                        return 0;
                    case IdNav when code == Ui.LBN_SELCHANGE:
                        SelectPage(Math.Max(0, Layout.Get(_nav, Ui.LB_GETCURSEL)));
                        return 0;
                }
                if (_onCommand.TryGetValue(id, out var h)) h(code);
                return 0;
            }
            case Ui.WM_HSCROLL:
                if (_onSlide.TryGetValue(l, out var slide)) slide(Layout.Get(l, Ui.TBM_GETPOS));
                return 0;
            case Ui.WM_CTLCOLORSTATIC:
            case Ui.WM_CTLCOLORBTN:
                Ui.SetBkColor(w, Ui.GetSysColor(5));
                return _whiteBrush;
            case Ui.WM_MEASUREITEM:
                ((MEASUREITEMSTRUCT*)l)->itemHeight = (uint)(64 * (_ui?.Scale ?? 1));
                return 1;
            case Ui.WM_DRAWITEM:
                DrawNavItem((DRAWITEMSTRUCT*)l);
                return 1;
            case Win32.WM_KEYDOWN when w == 0x1B:
            case Win32.WM_CLOSE:
                Finish(false);
                return 0;
            case Win32.WM_DESTROY:
                Current = null;
                foreach (var f in new[] { _font, _titleFont, _glyphFont }) if (f != 0) Win32.DeleteObject(f);
                return 0;
        }
        return Default(msg, w, l);
    }

    /// <summary>Navigation rail: Fluent glyph over the page name; the selected page gets an accent pill.</summary>
    private void DrawNavItem(DRAWITEMSTRUCT* d)
    {
        if (d->CtlID != IdNav || d->itemID >= Pages.Length) return;
        var r = d->rcItem;
        Ui.FillRect(d->hDC, &r, _whiteBrush);
        var selected = (d->itemState & 1) != 0;
        var s = _ui.Scale;
        if (selected)
        {
            byte ar = (byte)(_accent & 0xFF), ag = (byte)((_accent >> 8) & 0xFF), ab = (byte)((_accent >> 16) & 0xFF);
            var tint = Ui.CreateSolidBrush(Ui.Rgb((byte)(255 - (255 - ar) / 7), (byte)(255 - (255 - ag) / 7), (byte)(255 - (255 - ab) / 7)));
            var rgn = Ui.CreateRoundRectRgn(r.Left + (int)(4 * s), r.Top + (int)(3 * s), r.Right - (int)(4 * s), r.Bottom - (int)(3 * s), (int)(10 * s), (int)(10 * s));
            Ui.FillRgn(d->hDC, rgn, tint);
            Win32.DeleteObject(rgn);
            Win32.DeleteObject(tint);
            var accent = Ui.CreateSolidBrush(_accent);
            var bar = new RECT(r.Left + (int)(4 * s), r.Top + (int)(20 * s), r.Left + (int)(7 * s), r.Bottom - (int)(20 * s));
            Ui.FillRect(d->hDC, &bar, accent);
            Win32.DeleteObject(accent);
        }
        Ui.SetBkMode(d->hDC, 1);
        Ui.SetTextColor(d->hDC, selected ? _accent : Ui.Rgb(40, 40, 40));
        var old = Win32.SelectObject(d->hDC, _glyphFont);
        var gr = new RECT(r.Left, r.Top + (int)(10 * s), r.Right, r.Top + (int)(36 * s));
        Ui.DrawText(d->hDC, Pages[d->itemID].Glyph, -1, &gr, 0x1 | 0x20 | 0x4); // centre, single line, vcentre
        Win32.SelectObject(d->hDC, _font);
        Ui.SetTextColor(d->hDC, Ui.Rgb(30, 30, 30));
        var tr = new RECT(r.Left, r.Top + (int)(38 * s), r.Right, r.Bottom - (int)(4 * s));
        Ui.DrawText(d->hDC, Pages[d->itemID].Name, -1, &tr, 0x1 | 0x20 | 0x4);
        Win32.SelectObject(d->hDC, old);
    }
}
