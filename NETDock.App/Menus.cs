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

        Add(m, 98, "Dock Settings...");
        Sep(m);
        if (item is not null && item.Kind != ItemKind.Separator)
        {
            Add(m, 10, item.Kind == ItemKind.Stack ? "Open folder" : "Open");
            if (item.Kind != ItemKind.Stack) Add(m, 11, "Run as administrator");
            if (RecycleBin.Is(item)) Add(m, 17, "Empty Recycle Bin", enabled: RecycleBinService.IsFull);
            else Add(m, 12, "Open file location");
            if (item.Kind == ItemKind.Stack)
            {
                var st = item.Stack ??= new StackOptions();
                var view = Win32.CreatePopupMenu(); subs.Add(view);
                Add(view, 20, "Grid", st.View == StackView.Grid);
                Add(view, 21, "List", st.View == StackView.List);
                Add(view, 22, "Open folder on click", st.View == StackView.Open);
                Win32.AppendMenu(m, Win32.MF_POPUP, (nuint)view, "Stack view");
                var sort = Win32.CreatePopupMenu(); subs.Add(sort);
                Add(sort, 23, "Name", st.SortBy == StackSort.Name);
                Add(sort, 24, "Date modified", st.SortBy == StackSort.DateModified);
                Add(sort, 25, "Date created", st.SortBy == StackSort.DateCreated);
                Add(sort, 26, "Type", st.SortBy == StackSort.Kind);
                Sep(sort);
                Add(sort, 27, "Descending", st.SortDescending);
                Add(sort, 28, "Folders first", st.FoldersFirst);
                Add(sort, 29, "Show hidden files", st.ShowHidden);
                Win32.AppendMenu(m, Win32.MF_POPUP, (nuint)sort, "Stack sort");
                var cols = Win32.CreatePopupMenu(); subs.Add(cols);
                Add(cols, 30, "Automatic", st.Columns == 0);
                for (var c = 3; c <= 10; c++) Add(cols, (uint)(28 + c), $"{c} columns", st.Columns == c);
                Win32.AppendMenu(m, Win32.MF_POPUP, (nuint)cols, "Stack columns");
                Add(m, 39, "Change folder...");
            }
            Sep(m);
            Add(m, 13, "Icon Settings...");
            Add(m, 14, "Change icon...");
            Add(m, 15, "Use default icon", enabled: item.Icon is not null);
            Add(m, 16, "Remove from dock");
            Sep(m);
        }
        else if (item is not null)
        {
            Add(m, 16, "Remove separator");
            Sep(m);
        }

        var add = Sub("Add");
        Add(add, 42, "File or program...");
        Add(add, 41, "Folder stack...");
        Add(add, 40, "Separator");

        var edge = Sub("Position");
        Add(edge, 50, "Bottom", o.Edge == DockEdge.Bottom);
        Add(edge, 51, "Top", o.Edge == DockEdge.Top);
        Add(edge, 52, "Left", o.Edge == DockEdge.Left);
        Add(edge, 53, "Right", o.Edge == DockEdge.Right);
        Sep(edge);
        Add(edge, 54, "Primary monitor", o.Monitor == 0);
        var monitors = Monitors.All();
        for (var i = 0; i < monitors.Count && i < 8; i++)
        {
            var info = Monitors.Info(monitors[i]);
            Add(edge, (uint)(55 + i), $"Monitor {i + 1} ({info.Bounds.Width}x{info.Bounds.Height})", o.Monitor == i + 1);
        }

        var size = Sub("Icon size");
        for (var i = 0; i < IconSizes.Length; i++) Add(size, (uint)(100 + i), $"{IconSizes[i]} px", o.IconSize == IconSizes[i]);
        var gap = Sub("Icon spacing");
        for (var i = 0; i < Spacings.Length; i++) Add(gap, (uint)(160 + i), Spacings[i] == 0 ? "None" : $"{Spacings[i]} px", o.Spacing == Spacings[i]);
        var sepGap = Sub("Separator spacing");
        for (var i = 0; i < SepSpacings.Length; i++) Add(sepGap, (uint)(170 + i), SepSpacings[i] == 0 ? "None" : $"{SepSpacings[i]} px", o.SeparatorSpacing == SepSpacings[i]);
        var zoom = Sub("Zoom size");
        Add(zoom, 70, "Magnify on hover", o.Magnify);
        Sep(zoom);
        for (var i = 0; i < ZoomSizes.Length; i++) Add(zoom, (uint)(120 + i), $"{ZoomSizes[i]} px", o.MagnifiedSize == ZoomSizes[i], ZoomSizes[i] >= o.IconSize);

        var style = Sub("Style");
        Add(style, 140, "Background panel (RocketDock)", o.Style == DockStyle.Panel);
        Add(style, 141, "Platform shelf (macOS)", o.Style == DockStyle.Platform);
        Sep(style);
        var skins = SkinChoices(app);
        Add(style, 210, "Built-in panel", o.Style == DockStyle.Panel && o.Skin is null, o.Style == DockStyle.Panel);
        for (var i = 0; i < skins.Count && i < 80; i++)
            Add(style, (uint)(211 + i), skins[i].Name, o.Skin == skins[i].Reference, o.Style == DockStyle.Panel);
        _skinChoices = skins;

        var view2 = Sub("Options");
        Add(view2, 80, "Show labels", o.ShowLabels);
        Add(view2, 81, "Reflections", o.Reflections);
        Add(view2, 82, "Running indicators", o.ShowRunningIndicators);
        Add(view2, 85, "Clicking a running app switches to it", o.ActivateIfRunning);
        Add(view2, 83, "Auto-hide", o.AutoHide);
        Add(view2, 84, "Lock icons", o.Locked);
        Sep(view2);
        Add(view2, 95, "Start with Windows", App.StartsWithWindows);

        var data = Sub("Settings");
        Add(data, 91, "Export backup...");
        Add(data, 92, "Import backup...");
        Add(data, 90, "Import from RocketDock", RocketDockConfig.IsInstalled);
        Sep(data);
        Add(data, 93, "Open settings folder");
        Add(data, 94, app.Store.Location.IsPortable ? "Portable mode (settings in .\\Data)" : "Make portable (keep settings next to NETDock.exe)",
            app.Store.Location.IsPortable, !app.Store.Location.IsPortable);

        Sep(m);
        Add(m, 99, "Exit NETDock");

        Win32.SetForegroundWindow(dock.Hwnd);
        var cmd = (uint)Win32.TrackPopupMenuEx(m, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON, at.X, at.Y, dock.Hwnd, 0);
        Win32.PostMessage(dock.Hwnd, 0, 0, 0);
        Win32.DestroyMenu(m); // destroys submenus too
        if (cmd != 0) Run(app, dock, item, index, cmd);
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
                        if (Dialogs.PickFolder(dock.Hwnd, "Folder to show in this stack", app.Tokens.Expand(item.Target)) is { } f)
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
            case 98:
                SettingsWindow.Show(app);
                return;
            case 14 when item is not null:
                if (Dialogs.PickFile(dock.Hwnd, "Choose an icon", "Icons and images\0*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.exe;*.dll\0All files\0*.*\0",
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
            case 40: app.Settings.Items.Insert(Math.Min(at, app.Settings.Items.Count), new DockItem { Kind = ItemKind.Separator }); break;
            case 41:
                if (Dialogs.PickFolder(dock.Hwnd, "Folder to show as a stack") is { } folder && ItemFactory.FromPath(folder, app.Tokens) is { } s)
                    app.Settings.Items.Insert(Math.Min(at, app.Settings.Items.Count), s);
                break;
            case 42:
                if (Dialogs.PickFile(dock.Hwnd, "Add to dock", "Programs and shortcuts\0*.exe;*.lnk;*.bat;*.cmd;*.url\0All files\0*.*\0", dereferenceLinks: false) is { } p
                    && ItemFactory.FromPath(p, app.Tokens) is { } it)
                    app.Settings.Items.Insert(Math.Min(at, app.Settings.Items.Count), it);
                break;
            case >= 50 and <= 53: o.Edge = (DockEdge)(cmd - 50); break;
            case 54: o.Monitor = 0; break;
            case >= 55 and <= 62: o.Monitor = (int)cmd - 54; break;
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
            case 84: o.Locked = !o.Locked; break;
            case 85: o.ActivateIfRunning = !o.ActivateIfRunning; break;
            case 90:
                if (Win32.MessageBox(dock.Hwnd, "Replace the current dock with your RocketDock icons?\n\nChoose No to add them after the current icons.",
                        "NETDock", Win32.MB_YESNO | Win32.MB_ICONQUESTION) is var r)
                {
                    var summary = app.ImportRocketDock(replace: r == Win32.IDYES);
                    app.Save();
                    dock.Relayout();
                    if (summary is not null) Win32.MessageBox(dock.Hwnd, summary, "NETDock", Win32.MB_OK | Win32.MB_ICONINFORMATION);
                }
                return;
            case 91:
                if (Dialogs.SaveFile(dock.Hwnd, "Export NETDock backup", $"NETDock-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.zip") is { } zip)
                {
                    app.Store.Export(app.Settings, zip);
                    Win32.MessageBox(dock.Hwnd, $"Saved settings and custom icons to\n{zip}\n\nUse Settings > Import backup on another PC.", "NETDock", Win32.MB_OK | Win32.MB_ICONINFORMATION);
                }
                return;
            case 92:
                if (Dialogs.PickFile(dock.Hwnd, "Import NETDock backup", "NETDock backup\0*.zip\0") is { } inZip)
                {
                    try { app.Replace(app.Store.Import(inZip)); }
                    catch (Exception ex) { Win32.MessageBox(dock.Hwnd, ex.Message, "Import failed", Win32.MB_OK | Win32.MB_ICONWARNING); }
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
                $"Settings and icons copied to\n{data}\n\nNETDock will use that folder from its next start. Copy the whole NETDock folder to move your dock to another PC.",
                "NETDock", Win32.MB_OK | Win32.MB_ICONINFORMATION);
        }
        catch (Exception ex)
        {
            Win32.MessageBox(dock.Hwnd, $"Could not write to {data}:\n{ex.Message}\n\nMove NETDock somewhere you can write to (not Program Files) to use portable mode.",
                "NETDock", Win32.MB_OK | Win32.MB_ICONWARNING);
        }
    }
}

public static unsafe class Dialogs
{
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
