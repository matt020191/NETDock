using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>Native popup menus: the item menu (right-click an icon) and the dock menu.</summary>
public static unsafe class Menus
{
    private static readonly int[] IconSizes = [32, 40, 44, 48, 56, 64, 72, 80, 96, 128];
    private static readonly int[] Spacings = [0, 2, 4, 6, 8, 12];
    private static readonly int[] SepSpacings = [0, 2, 4, 6, 8, 12, 16, 24];
    private static List<(string Name, string Reference, string? Source)> _skinChoices = [];

    /// <summary>Skins already in the data folder, then the installed RocketDock ones (copied in when picked).</summary>
    public static List<(string Name, string Reference, string? Source)> SkinChoices(App app)
    {
        var list = new List<(string Name, string Reference, string? Source)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var own = Path.Combine(app.Store.Location.Folder, "skins");
        if (Directory.Exists(own))
            foreach (var d in Directory.EnumerateDirectories(own).Order())
                if (File.Exists(Path.Combine(d, "background.ini")) && seen.Add(Path.GetFileName(d)))
                    list.Add((Path.GetFileName(d), "skins/" + Path.GetFileName(d), null));
        if (RocketDockConfig.Folder() is { } rd && Directory.Exists(Path.Combine(rd, "Skins")))
            foreach (var d in Directory.EnumerateDirectories(Path.Combine(rd, "Skins")).Order())
                if (File.Exists(Path.Combine(d, "background.ini")) && seen.Add(Path.GetFileName(d)))
                    list.Add((Path.GetFileName(d) + "  (RocketDock)", "skins/" + Path.GetFileName(d), d));
        return list;
    }

    private static readonly int[] ZoomSizes = [64, 80, 96, 112, 128, 160, 192, 256];

    public static void ShowDockMenu(App app, DockWindow dock, DockItem? item, int index, POINT at)
    {
        var o = app.Settings.Dock;
        var m = Win32.CreatePopupMenu();
        var subs = new List<nint>();
        nint Sub(string text)
        {
            var s = Win32.CreatePopupMenu();
            subs.Add(s);
            Win32.AppendMenu(m, Win32.MF_POPUP, (nuint)s, text);
            return s;
        }
        void Add(nint menu, uint id, string text, bool check = false, bool enabled = true) =>
            Win32.AppendMenu(menu, (check ? Win32.MF_CHECKED : 0) | (enabled ? 0 : Win32.MF_GRAYED), id, text);
        void Sep(nint menu) => Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);

        Add(m, 98, L.T("Dock Settings..."));
        Sep(m);
        if (item is not null && item.Kind != ItemKind.Separator)
        {
            Add(m, 10, item.Kind == ItemKind.Stack ? L.T("Open folder") : L.T("Open"));
            if (item.Kind != ItemKind.Stack) Add(m, 11, L.T("Run as administrator"));
            if (RecycleBin.Is(item)) Add(m, 17, L.T("Empty Recycle Bin"), enabled: RecycleBinService.IsFull);
            else Add(m, 12, L.T("Open file location"));
            if (item.Kind == ItemKind.Stack)
            {
                var st = item.Stack ??= new StackOptions();
                var view = Win32.CreatePopupMenu(); subs.Add(view);
                Add(view, 20, L.T("Grid"), st.View == StackView.Grid);
                Add(view, 21, L.T("List"), st.View == StackView.List);
                Add(view, 22, L.T("Open folder on click"), st.View == StackView.Open);
                Win32.AppendMenu(m, Win32.MF_POPUP, (nuint)view, L.T("Stack view"));
                var sort = Win32.CreatePopupMenu(); subs.Add(sort);
                Add(sort, 23, L.T("Name"), st.SortBy == StackSort.Name);
                Add(sort, 24, L.T("Date modified"), st.SortBy == StackSort.DateModified);
                Add(sort, 25, L.T("Date created"), st.SortBy == StackSort.DateCreated);
                Add(sort, 26, L.T("Type"), st.SortBy == StackSort.Kind);
                Sep(sort);
                Add(sort, 27, L.T("Descending"), st.SortDescending);
                Add(sort, 28, L.T("Folders first"), st.FoldersFirst);
                Add(sort, 29, L.T("Show hidden files"), st.ShowHidden);
                Win32.AppendMenu(m, Win32.MF_POPUP, (nuint)sort, L.T("Stack sort"));
                var cols = Win32.CreatePopupMenu(); subs.Add(cols);
                Add(cols, 30, L.T("Automatic"), st.Columns == 0);
                for (var c = 3; c <= 10; c++) Add(cols, (uint)(28 + c), L.F("{0} columns", c), st.Columns == c);
                Win32.AppendMenu(m, Win32.MF_POPUP, (nuint)cols, L.T("Stack columns"));
                Add(m, 39, L.T("Change folder..."));
            }
            Sep(m);
            Add(m, 13, L.T("Icon Settings..."));
            Add(m, 14, L.T("Change icon..."));
            Add(m, 15, L.T("Use default icon"), enabled: item.Icon is not null);
            Add(m, 16, L.T("Remove from dock"));
            Sep(m);
        }
        else if (item is not null)
        {
            Add(m, 16, L.T("Remove separator"));
            Sep(m);
        }

        var add = Sub(L.T("Add"));
        Add(add, 42, L.T("File or program..."));
        Add(add, 41, L.T("Folder stack..."));
        Add(add, 40, L.T("Separator"));
        Sep(add);
        Add(add, 43, L.T("Dock Settings icon"));
        Add(add, 44, L.T("Quit icon"));
        Add(add, 45, L.T("Search icon"));

        var edge = Sub(L.T("Position"));
        Add(edge, 50, L.T("Bottom"), o.Edge == DockEdge.Bottom);
        Add(edge, 51, L.T("Top"), o.Edge == DockEdge.Top);
        Add(edge, 52, L.T("Left"), o.Edge == DockEdge.Left);
        Add(edge, 53, L.T("Right"), o.Edge == DockEdge.Right);
        Sep(edge);
        Add(edge, 54, L.T("Primary monitor"), o.Monitor == 0);
        var monitors = Monitors.All();
        for (var i = 0; i < monitors.Count && i < 8; i++)
        {
            var info = Monitors.Info(monitors[i]);
            Add(edge, (uint)(55 + i), L.F("Monitor {0} ({1}x{2})", i + 1, info.Bounds.Width, info.Bounds.Height), o.Monitor == i + 1);
        }
        if (monitors.Count > 1) Add(edge, 63, L.T("All monitors (span)"), o.Monitor == MonitorSpan.All);
        Sep(edge);
        Add(edge, 190, L.T("Always on top"), o.Layer == DockLayer.AlwaysOnTop);
        Add(edge, 191, L.T("Normal"), o.Layer == DockLayer.Normal);
        Add(edge, 192, L.T("Always on bottom"), o.Layer == DockLayer.AlwaysOnBottom);
        Add(edge, 193, L.T("Pop up on mouse-over"), o.PopUp, o.Layer != DockLayer.AlwaysOnTop);

        var size = Sub(L.T("Icon size"));
        for (var i = 0; i < IconSizes.Length; i++) Add(size, (uint)(100 + i), $"{IconSizes[i]} px", o.IconSize == IconSizes[i]);
        var gap = Sub(L.T("Icon spacing"));
        for (var i = 0; i < Spacings.Length; i++) Add(gap, (uint)(160 + i), Spacings[i] == 0 ? L.T("None") : $"{Spacings[i]} px", o.Spacing == Spacings[i]);
        var sepGap = Sub(L.T("Separator spacing"));
        for (var i = 0; i < SepSpacings.Length; i++) Add(sepGap, (uint)(170 + i), SepSpacings[i] == 0 ? L.T("None") : $"{SepSpacings[i]} px", o.SeparatorSpacing == SepSpacings[i]);
        var zoom = Sub(L.T("Zoom size"));
        Add(zoom, 70, L.T("Magnify on hover"), o.Magnify);
        Sep(zoom);
        for (var i = 0; i < ZoomSizes.Length; i++) Add(zoom, (uint)(120 + i), $"{ZoomSizes[i]} px", o.MagnifiedSize == ZoomSizes[i], ZoomSizes[i] >= o.IconSize);

        var style = Sub(L.T("Style"));
        Add(style, 140, L.T("Background panel (RocketDock)"), o.Style == DockStyle.Panel);
        Add(style, 141, L.T("Platform shelf (macOS)"), o.Style == DockStyle.Platform);
        Sep(style);
        var skins = SkinChoices(app);
        Add(style, 210, L.T("Built-in panel"), o.Style == DockStyle.Panel && o.Skin is null, o.Style == DockStyle.Panel);
        for (var i = 0; i < skins.Count && i < 80; i++)
            Add(style, (uint)(211 + i), skins[i].Name, o.Skin == skins[i].Reference, o.Style == DockStyle.Panel);
        _skinChoices = skins;

        var view2 = Sub(L.T("Options"));
        Add(view2, 80, L.T("Show labels"), o.ShowLabels);
        Add(view2, 81, L.T("Reflections"), o.Reflections);
        Add(view2, 82, L.T("Running indicators"), o.ShowRunningIndicators);
        Add(view2, 85, L.T("Clicking a running app switches to it"), o.ActivateIfRunning);
        Add(view2, 83, L.T("Auto-hide"), o.AutoHide);
        Add(view2, 86, L.T("Minimise windows to the dock"), o.MinimizeToDock);
        Add(view2, 87, L.T("Live window previews"), o.LivePreviews);
        Add(view2, 88, L.T("Show running apps"), o.ShowRunningApps);
        Add(view2, 84, L.T("Lock icons (Ctrl-drag still moves them)"), o.Locked);
        Sep(view2);
        Add(view2, 95, L.T("Start with Windows"), App.StartsWithWindows);

        var data = Sub(L.T("Settings"));
        Add(data, 91, L.T("Export backup..."));
        Add(data, 92, L.T("Import backup..."));
        Add(data, 90, L.T("Import from RocketDock"), RocketDockConfig.IsInstalled);
        Sep(data);
        Add(data, 93, L.T("Open settings folder"));
        Add(data, 94, app.Store.Location.IsPortable ? "Portable mode (settings in .\\Data)" : L.T("Make portable (keep settings next to NETDock.exe)"),
            app.Store.Location.IsPortable, !app.Store.Location.IsPortable);

        Sep(m);
        Add(m, 96, Hotkey.Parse(o.SearchHotkey) is null ? L.T("Search...") : L.T("Search...") + "\t" + o.SearchHotkey);
        Add(m, 97, Hotkey.Parse(o.ToggleHotkey) is null ? L.T("Hide dock") : L.T("Hide dock") + "\t" + o.ToggleHotkey);
        Add(m, 99, L.T("Exit NETDock"));

        Win32.SetForegroundWindow(dock.Hwnd);
        var cmd = (uint)Win32.TrackPopupMenuEx(m, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON, at.X, at.Y, dock.Hwnd, 0);
        Win32.PostMessage(dock.Hwnd, 0, 0, 0);
        Win32.DestroyMenu(m); // destroys submenus too
        if (cmd != 0) Run(app, dock, item, index, cmd);
    }

    /// <summary>Right-click on a running program that isn't pinned.</summary>
    public static void ShowRunningMenu(App app, DockWindow dock, DockItem item, POINT at)
    {
        var m = Win32.CreatePopupMenu();
        Win32.AppendMenu(m, 0, 1, L.T("Pin to dock"));
        Win32.AppendMenu(m, 0, 2, L.T("Close window"));
        Win32.AppendMenu(m, Win32.MF_SEPARATOR, 0, null);
        Win32.AppendMenu(m, app.Settings.Dock.ShowRunningApps ? Win32.MF_CHECKED : 0, 3, L.T("Show running apps"));
        Win32.SetForegroundWindow(dock.Hwnd);
        var cmd = Win32.TrackPopupMenuEx(m, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON, at.X, at.Y, dock.Hwnd, 0);
        Win32.PostMessage(dock.Hwnd, 0, 0, 0);
        Win32.DestroyMenu(m);
        switch (cmd)
        {
            case 1: dock.PinRunning(item); break;
            case 2: dock.CloseWindowsOf(item); break;
            case 3: app.Settings.Dock.ShowRunningApps = false; app.Save(); dock.Relayout(); break;
        }
    }

    /// <summary>Run a dock-level menu command (no item) from elsewhere, e.g. the settings window.</summary>
    public static void Run(App app, uint cmd) => Run(app, app.Dock, null, -1, cmd);

    private static void Run(App app, DockWindow dock, DockItem? item, int index, uint cmd)
    {
        var o = app.Settings.Dock;
        var at = index >= 0 ? index + 1 : app.Settings.Items.Count;
        var relayout = true;
        switch (cmd)
        {
            case 10 when item is not null:
                if (item.Kind == ItemKind.Stack) Launcher.Open(app.Tokens.Expand(item.Target)!);
                else Launcher.Launch(item, app.Tokens);
                return;
            case 11 when item is not null: Launcher.Launch(item, app.Tokens, elevate: true); return;
            case 12 when item is not null:
            {
                var t = app.Tokens.Expand(item.Kind == ItemKind.Shortcut && !File.Exists(app.Tokens.Expand(item.Target)) ? item.Fallback?.Target : item.Target);
                if (t is not null) Launcher.ShowInExplorer(t);
                return;
            }
            case >= 20 and <= 39 when item?.Stack is { } st:
                switch (cmd)
                {
                    case 20: st.View = StackView.Grid; break;
                    case 21: st.View = StackView.List; break;
                    case 22: st.View = StackView.Open; break;
                    case 23: st.SortBy = StackSort.Name; break;
                    case 24: st.SortBy = StackSort.DateModified; break;
                    case 25: st.SortBy = StackSort.DateCreated; break;
                    case 26: st.SortBy = StackSort.Kind; break;
                    case 27: st.SortDescending = !st.SortDescending; break;
                    case 28: st.FoldersFirst = !st.FoldersFirst; break;
                    case 29: st.ShowHidden = !st.ShowHidden; break;
                    case 30: st.Columns = 0; break;
                    case 39:
                        if (Dialogs.PickFolder(dock.Hwnd, L.T("Folder to show in this stack"), app.Tokens.Expand(item.Target)) is { } f)
                        {
                            dock.ForgetIcon(item);
                            item.Target = app.Tokens.Tokenize(f)!;
                            if (string.IsNullOrWhiteSpace(item.Name) || item.Name == "Stack") item.Name = Path.GetFileName(f.TrimEnd('\\'));
                        }
                        break;
                    default: st.Columns = (int)cmd - 28; break;
                }
                relayout = false;
                break;
            case 13 when item is not null:
                IconSettingsDialog.Show(app, item);
                return;
            case 96: app.Search.Show(); return;
            case 97: dock.ToggleVisible(); return;
            case 98:
                SettingsWindow.Show(app);
                return;
            case 14 when item is not null:
                if (Dialogs.PickFile(dock.Hwnd, L.T("Choose an icon"), "Icons and images\0*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.exe;*.dll\0All files\0*.*\0",
                        initial: app.Store.ResolveDataIcon(item.Icon) ?? app.Store.Location.IconsFolder) is { } file)
                {
                    dock.ForgetIcon(item);
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    // Images are copied into the dock's own folder so they travel with it.
                    item.Icon = ext is ".exe" or ".dll" ? app.Tokens.Tokenize(file) + ",0" : app.Store.ImportIcon(file);
                }
                break;
            case 15 when item is not null: dock.ForgetIcon(item); item.Icon = null; break;
            case 16 when item is not null: app.Settings.Items.Remove(item); break;
            case 17: RecycleBinService.Empty(dock.Hwnd); return;
            case 43: app.Settings.Items.Insert(Math.Min(at, app.Settings.Items.Count), SpecialItems.Create(SpecialItems.Settings)); break;
            case 45: app.Settings.Items.Insert(Math.Min(at, app.Settings.Items.Count), SpecialItems.Create(SpecialItems.Search)); break;
            case 44: app.Settings.Items.Insert(Math.Min(at, app.Settings.Items.Count), SpecialItems.Create(SpecialItems.Quit)); break;
            case 40: app.Settings.Items.Insert(Math.Min(at, app.Settings.Items.Count), new DockItem { Kind = ItemKind.Separator }); break;
            case 41:
                if (Dialogs.PickFolder(dock.Hwnd, L.T("Folder to show as a stack")) is { } folder && ItemFactory.FromPath(folder, app.Tokens) is { } s)
                    app.Settings.Items.Insert(Math.Min(at, app.Settings.Items.Count), s);
                break;
            case 42:
                if (Dialogs.PickFile(dock.Hwnd, L.T("Add to dock"), "Programs and shortcuts\0*.exe;*.lnk;*.bat;*.cmd;*.url\0All files\0*.*\0", dereferenceLinks: false) is { } p
                    && ItemFactory.FromPath(p, app.Tokens) is { } it)
                    app.Settings.Items.Insert(Math.Min(at, app.Settings.Items.Count), it);
                break;
            case >= 50 and <= 53: o.Edge = (DockEdge)(cmd - 50); break;
            case 54: o.Monitor = 0; break;
            case >= 190 and <= 192: o.Layer = (DockLayer)(cmd - 190); break;
            case 193: o.PopUp = !o.PopUp; break;
            case >= 55 and <= 62: o.Monitor = (int)cmd - 54; break;
            case 63: o.Monitor = MonitorSpan.All; break;
            case >= 100 and < 110: o.IconSize = IconSizes[cmd - 100]; o.MagnifiedSize = Math.Max(o.MagnifiedSize, o.IconSize); break;
            case 70: o.Magnify = !o.Magnify; break;
            case >= 120 and < 130: o.MagnifiedSize = ZoomSizes[cmd - 120]; o.Magnify = true; break;
            case 140: o.Style = DockStyle.Panel; break;
            case 141: o.Style = DockStyle.Platform; break;
            case 210: o.Skin = null; break;
            case >= 211 and < 291 when cmd - 211 < _skinChoices.Count:
            {
                var choice = _skinChoices[(int)cmd - 211];
                // Copied into the data folder, so the look travels with a portable dock or backup.
                o.Skin = choice.Source is { } src ? app.Store.ImportSkin(src) : choice.Reference;
                break;
            }
            case >= 160 and < 170 when cmd - 160 < Spacings.Length: o.Spacing = Spacings[cmd - 160]; break;
            case >= 170 and < 180 when cmd - 170 < SepSpacings.Length: o.SeparatorSpacing = SepSpacings[cmd - 170]; break;
            case 80: o.ShowLabels = !o.ShowLabels; break;
            case 81: o.Reflections = !o.Reflections; break;
            case 82: o.ShowRunningIndicators = !o.ShowRunningIndicators; break;
            case 83: o.AutoHide = !o.AutoHide; break;
            case 88: o.ShowRunningApps = !o.ShowRunningApps; break;
            case 87: o.LivePreviews = !o.LivePreviews; if (!o.LivePreviews) app.Preview.Hide(); break;
            case 86: o.MinimizeToDock = !o.MinimizeToDock; dock.RefreshMinimized(); break;
            case 84: o.Locked = !o.Locked; break;
            case 85: o.ActivateIfRunning = !o.ActivateIfRunning; break;
            case 90:
                if (Win32.MessageBox(dock.Hwnd, L.T("Replace the current dock with your RocketDock icons?\n\nChoose No to add them after the current icons."),
                        "NETDock", Win32.MB_YESNO | Win32.MB_ICONQUESTION) is var r)
                {
                    var summary = app.ImportRocketDock(replace: r == Win32.IDYES);
                    app.Save();
                    dock.Relayout();
                    if (summary is not null) Win32.MessageBox(dock.Hwnd, summary, "NETDock", Win32.MB_OK | Win32.MB_ICONINFORMATION);
                }
                return;
            case 91:
                if (Dialogs.SaveFile(dock.Hwnd, L.T("Export NETDock backup"), $"NETDock-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.zip") is { } zip)
                {
                    app.Store.Export(app.Settings, zip);
                    Win32.MessageBox(dock.Hwnd, L.F("Saved settings and custom icons to\n{0}\n\nUse Settings > Import backup on another PC.", zip), "NETDock", Win32.MB_OK | Win32.MB_ICONINFORMATION);
                }
                return;
            case 92:
                if (Dialogs.PickFile(dock.Hwnd, L.T("Import NETDock backup"), "NETDock backup\0*.zip\0") is { } inZip)
                {
                    try { app.Replace(app.Store.Import(inZip)); }
                    catch (Exception ex) { Win32.MessageBox(dock.Hwnd, ex.Message, L.T("Import failed"), Win32.MB_OK | Win32.MB_ICONWARNING); }
                }
                return;
            case 93: Directory.CreateDirectory(app.Store.Location.Folder); Launcher.Open(app.Store.Location.Folder); return;
            case 94: MakePortable(app, dock); return;
            case 95: App.StartsWithWindows = !App.StartsWithWindows; return;
            case 99: dock.Close(); return;
            default: return;
        }
        app.Save();
        if (relayout) dock.Relayout(); else dock.Render();
    }

    private static void MakePortable(App app, DockWindow dock)
    {
        var data = Path.Combine(app.ExeFolder, "Data");
        try
        {
            app.Save();
            Directory.CreateDirectory(Path.Combine(data, "icons"));
            File.Copy(app.Store.Location.SettingsFile, Path.Combine(data, "settings.json"), overwrite: true);
            if (Directory.Exists(app.Store.Location.IconsFolder))
                foreach (var f in Directory.EnumerateFiles(app.Store.Location.IconsFolder))
                    SettingsStore.CopyOver(f, Path.Combine(data, "icons", Path.GetFileName(f)));
            Win32.MessageBox(dock.Hwnd,
                L.F("Settings and icons copied to\n{0}\n\nNETDock will use that folder from its next start. Copy the whole NETDock folder to move your dock to another PC.", data),
                "NETDock", Win32.MB_OK | Win32.MB_ICONINFORMATION);
        }
        catch (Exception ex)
        {
            Win32.MessageBox(dock.Hwnd, L.F("Could not write to {0}:\n{1}\n\nMove NETDock somewhere you can write to (not Program Files) to use portable mode.", data, ex.Message),
                "NETDock", Win32.MB_OK | Win32.MB_ICONWARNING);
        }
    }
}

public static unsafe partial class Dialogs
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CHOOSECOLORW
    {
        public uint lStructSize; public nint hwndOwner, hInstance; public uint rgbResult; public uint* lpCustColors;
        public uint Flags; public nint lCustData, lpfnHook, lpTemplateName;
    }
    [System.Runtime.InteropServices.LibraryImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool ChooseColor(CHOOSECOLORW* c);
    private static readonly uint[] CustomColors = new uint[16];

    /// <summary>Windows colour picker. Takes and returns 0xRRGGBB; null if cancelled.</summary>
    public static uint? PickColor(nint owner, uint rgb)
    {
        static uint Swap(uint v) => ((v & 0xFF) << 16) | (v & 0xFF00) | ((v >> 16) & 0xFF); // RRGGBB <-> COLORREF
        fixed (uint* cust = CustomColors)
        {
            var c = new CHOOSECOLORW { lStructSize = (uint)sizeof(CHOOSECOLORW), hwndOwner = owner, rgbResult = Swap(rgb), lpCustColors = cust, Flags = 0x1 | 0x2 /* RGBINIT | FULLOPEN */ };
            return ChooseColor(&c) ? Swap(c.rgbResult) : null;
        }
    }

    /// <param name="initial">A file or folder to open the dialog at (typically the field's current value).</param>
    public static string? PickFile(nint owner, string title, string filter, bool dereferenceLinks = true, string? initial = null)
    {
        var buf = new char[2048];
        // Pre-filling the file box is the reliable way to choose the starting folder: Windows
        // ignores lpstrInitialDir in favour of the last folder used, once there is one.
        if (initial is { Length: > 0 and < 2000 })
        {
            if (File.Exists(initial)) initial.CopyTo(0, buf, 0, initial.Length);
            else if (Directory.Exists(initial)) (initial.TrimEnd('\\') + "\\").CopyTo(0, buf, 0, initial.TrimEnd('\\').Length + 1);
            else if (Path.GetDirectoryName(initial) is { } d && Directory.Exists(d)) (d.TrimEnd('\\') + "\\").CopyTo(0, buf, 0, d.TrimEnd('\\').Length + 1);
        }
        fixed (char* b = buf) fixed (char* f = filter) fixed (char* t = title)
        {
            var o = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW), hwndOwner = owner, lpstrFilter = f, lpstrFile = b, nMaxFile = 2048, lpstrTitle = t,
                Flags = 0x1000 /* FILEMUSTEXIST */ | 0x800 /* PATHMUSTEXIST */ | 0x8 /* NOCHANGEDIR */ | 0x80000 /* EXPLORER */ | (dereferenceLinks ? 0 : 0x100000u /* NODEREFERENCELINKS */),
            };
            return Win32.GetOpenFileName(&o) ? new string(b) : null;
        }
    }

    public static string? SaveFile(nint owner, string title, string defaultName)
    {
        var buf = new char[2048];
        defaultName.CopyTo(0, buf, 0, defaultName.Length);
        fixed (char* b = buf) fixed (char* f = "Zip archive\0*.zip\0") fixed (char* t = title) fixed (char* ext = "zip")
        {
            var o = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW), hwndOwner = owner, lpstrFilter = f, lpstrFile = b, nMaxFile = 2048, lpstrTitle = t, lpstrDefExt = ext,
                Flags = 0x2 /* OVERWRITEPROMPT */ | 0x8 | 0x80000,
            };
            return Win32.GetSaveFileName(&o) ? new string(b) : null;
        }
    }

    /// <summary>Selects <c>lParam</c> (a folder path) once the browse dialog is up.</summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int BrowseCallback(nint hwnd, uint msg, nint lParam, nint data)
    {
        if (msg == 1 /* BFFM_INITIALIZED */ && data != 0) Win32.SendMessage(hwnd, 0x467 /* BFFM_SETSELECTIONW */, 1, data);
        return 0;
    }

    /// <param name="initial">Folder to start at; a file path starts at its folder.</param>
    public static string? PickFolder(nint owner, string title, string? initial = null)
    {
        var name = stackalloc char[260];
        string? start = null;
        if (initial is { Length: > 0 })
            start = Directory.Exists(initial) ? initial : Path.GetDirectoryName(initial) is { } d && Directory.Exists(d) ? d : null;
        fixed (char* t = title)
        fixed (char* st = start)
        {
            var bi = new BROWSEINFOW
            {
                hwndOwner = owner, pszDisplayName = name, lpszTitle = t, ulFlags = 0x1 /* RETURNONLYFSDIRS */ | 0x40 /* NEWDIALOGSTYLE */,
                lpfn = (nint)(delegate* unmanaged<nint, uint, nint, nint, int>)&BrowseCallback,
                lParam = start is null ? 0 : (nint)st,
            };
            var pidl = Win32.SHBrowseForFolder(&bi);
            if (pidl == 0) return null;
            var path = stackalloc char[1024];
            var ok = Win32.SHGetPathFromIDList(pidl, path);
            Win32.CoTaskMemFree(pidl);
            return ok ? new string(path) : null;
        }
    }
}
